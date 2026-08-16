using System.Net.Http;
using System.Text;
using System.Threading;
using TriffView.Alerts;
using Xunit;

namespace TriffView.Tests;

public class CombatLogUploadStubServerTests
{
    [Fact]
    public async Task SuccessfulUpload_SendsExpectedMultipartFraming()
    {
        var zipBytes = Encoding.UTF8.GetBytes("fake zip contents for framing test");
        var zipPath = Path.Combine(Path.GetTempPath(), $"triffview-fight-{Guid.NewGuid():N}.zip");
        var stagingPath = zipPath + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(zipPath, zipBytes);
        await File.WriteAllBytesAsync(stagingPath, Array.Empty<byte>());

        using var server = new StubWebhookServer { StatusCode = 204 };
        // The token segment is whatever this instance of the shared stub happens to use --
        // read it back from the Uri rather than hard-coding it, so this assertion still means
        // something if StubWebhookServer ever changes its path shape.
        var token = server.Uri.Segments[^1];
        const string content = "3 pilots, 2026-08-16 00:00-01:00 UTC. 4 files dropped.";

        using var http = new HttpClient();
        CombatLogUploadResult result;
        try
        {
            result = await CombatLogUpload.UploadAsync(http, server.Uri, zipPath, content, CancellationToken.None);
        }
        finally
        {
            // Mirrors the design's Flow step 6 (delete the finished archive on every path)
            // plus the orphaned-temporaries sweep (delete any matching staging file too).
            File.Delete(zipPath);
            File.Delete(stagingPath);
        }

        Assert.True(result.Succeeded, result.Message);
        Assert.NotNull(server.LastContentType);
        Assert.StartsWith("multipart/form-data", server.LastContentType!, StringComparison.OrdinalIgnoreCase);

        var parts = ParseMultipart(server.LastRequestBytes!, ExtractBoundary(server.LastContentType!));
        Assert.True(parts.ContainsKey("payload_json"));
        Assert.True(parts.ContainsKey("files[0]"));
        Assert.Equal(zipBytes, parts["files[0]"]);

        var payloadText = Encoding.UTF8.GetString(parts["payload_json"]);
        Assert.Contains("dropped", payloadText, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(token, payloadText, StringComparison.Ordinal);
        Assert.DoesNotContain(token, result.Message, StringComparison.Ordinal);
        // Not cleanup coverage -- the finally above deletes both files unconditionally,
        // so these can only ever observe this test's own tidy-up (real coverage is in
        // CombatLogUploadFlowTests). What they retain is a leaked-handle check only a
        // live socket can give: if UploadAsync ever failed to close its FileStream, the
        // File.Delete in the finally would throw IOException and fail the test.
        Assert.False(File.Exists(zipPath));
        Assert.False(File.Exists(stagingPath));
    }

    [Fact]
    public async Task FailedUpload_ReturnsFailureResult()
    {
        var zipBytes = Encoding.UTF8.GetBytes("fake zip contents for failure-path test");
        var zipPath = Path.Combine(Path.GetTempPath(), $"triffview-fight-{Guid.NewGuid():N}.zip");
        var stagingPath = zipPath + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(zipPath, zipBytes);
        await File.WriteAllBytesAsync(stagingPath, Array.Empty<byte>());

        using var server = new StubWebhookServer { StatusCode = 500 };
        var token = server.Uri.Segments[^1];
        const string content = "1 pilot, 2026-08-16 02:00-02:30 UTC.";

        using var http = new HttpClient();
        CombatLogUploadResult result;
        try
        {
            result = await CombatLogUpload.UploadAsync(http, server.Uri, zipPath, content, CancellationToken.None);
        }
        finally
        {
            File.Delete(zipPath);
            File.Delete(stagingPath);
        }

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(token, result.Message, StringComparison.Ordinal);
        // Not cleanup coverage -- the finally above deletes both files unconditionally,
        // so these can only ever observe this test's own tidy-up (real coverage is in
        // CombatLogUploadFlowTests). What they retain is a leaked-handle check only a
        // live socket can give: if UploadAsync ever failed to close its FileStream, the
        // File.Delete in the finally would throw IOException and fail the test.
        Assert.False(File.Exists(zipPath));
        Assert.False(File.Exists(stagingPath));
    }

    private static string ExtractBoundary(string contentType)
    {
        const string marker = "boundary=";
        var index = contentType.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            throw new InvalidOperationException($"no boundary in content-type: {contentType}");
        }
        var boundary = contentType[(index + marker.Length)..].Trim('"');
        var semicolon = boundary.IndexOf(';');
        return semicolon >= 0 ? boundary[..semicolon] : boundary;
    }

    private static Dictionary<string, byte[]> ParseMultipart(byte[] body, string boundary)
    {
        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var headerTerminator = Encoding.ASCII.GetBytes("\r\n\r\n");
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        var start = IndexOf(body, delimiter, 0);
        while (start >= 0)
        {
            var partStart = start + delimiter.Length;
            if (partStart + 1 < body.Length && body[partStart] == (byte)'-' && body[partStart + 1] == (byte)'-')
            {
                break; // closing boundary "--boundary--"
            }

            var next = IndexOf(body, delimiter, partStart);
            if (next < 0)
            {
                break;
            }

            var partBytes = body[partStart..next];
            var headerEnd = IndexOf(partBytes, headerTerminator, 0);
            if (headerEnd >= 0)
            {
                var headerText = Encoding.ASCII.GetString(partBytes, 0, headerEnd);
                var name = ExtractName(headerText);
                var contentStart = headerEnd + headerTerminator.Length;
                var contentEnd = partBytes.Length;
                if (contentEnd >= 2 && partBytes[contentEnd - 2] == (byte)'\r' && partBytes[contentEnd - 1] == (byte)'\n')
                {
                    contentEnd -= 2; // trailing CRLF before the next boundary marker
                }
                if (name is not null)
                {
                    result[name] = partBytes[contentStart..contentEnd];
                }
            }

            start = next;
        }

        return result;
    }

    private static string? ExtractName(string headerText)
    {
        // .NET's MultipartFormDataContent only quotes the Content-Disposition
        // name when it contains characters a bare token can't hold (e.g.
        // "files[0]"); a plain name like "payload_json" comes back as
        // name=payload_json with no quotes at all. Both forms have to be
        // handled or the unquoted case silently fails to match.
        const string marker = "name=";
        var index = headerText.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }
        var nameStart = index + marker.Length;
        if (nameStart < headerText.Length && headerText[nameStart] == '"')
        {
            nameStart++;
            var quotedEnd = headerText.IndexOf('"', nameStart);
            return quotedEnd < 0 ? null : headerText[nameStart..quotedEnd];
        }

        var nameEnd = headerText.IndexOfAny(new[] { ';', '\r', '\n' }, nameStart);
        return nameEnd < 0 ? headerText[nameStart..] : headerText[nameStart..nameEnd];
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        for (var i = from; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                return i;
            }
        }
        return -1;
    }
}
