using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TriffView.Alerts;
using Xunit;

namespace TriffView.Tests;

/// <summary>
/// The webhook URL is a bearer credential shaped like a URL. Every test here
/// backs one of two guarantees: that only a real Discord webhook is accepted
/// (an allowlist with no override), and that the token segment of it can never
/// leak into a description or an error message.
/// </summary>
public class CombatLogUploadTests
{
    // ---- DiscordWebhook.TryParse ----

    [Theory]
    [InlineData("https://discord.com/api/webhooks/123456789/abcDEF-token_123")]
    [InlineData("https://discordapp.com/api/webhooks/123456789/abcDEF-token_123")]
    [InlineData("https://ptb.discord.com/api/webhooks/123456789/abcDEF-token_123")]
    [InlineData("https://canary.discord.com/api/webhooks/123456789/abcDEF-token_123")]
    public void AGenuineWebhookUrlOnAnyAllowedHostIsAccepted(string url)
    {
        Assert.True(DiscordWebhook.TryParse(url, out var webhook, out var error));
        Assert.Equal("", error);
        Assert.Equal(url, webhook.AbsoluteUri);
    }

    [Fact]
    public void PlainHttpIsRejected()
    {
        // A webhook URL is a bearer credential; sending it over http would leak
        // it to anything on the network path.
        Assert.False(DiscordWebhook.TryParse(
            "http://discord.com/api/webhooks/123456789/abcDEF-token_123", out _, out var error));
        Assert.Contains("https", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://discord.co/api/webhooks/123456789/abcDEF-token_123")]
    [InlineData("https://evil.example.com/api/webhooks/123456789/abcDEF-token_123")]
    [InlineData("https://discord.com.evil.example.com/api/webhooks/123456789/abcDEF-token_123")]
    public void AnyHostOffTheAllowlistIsRejected(string url)
    {
        // No override toggle exists for a reason -- see the design doc. A host
        // check that could be fooled by a lookalike domain would defeat the
        // whole point of the allowlist.
        Assert.False(DiscordWebhook.TryParse(url, out _, out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void AMissingTokenSegmentIsRejected()
    {
        Assert.False(DiscordWebhook.TryParse(
            "https://discord.com/api/webhooks/123456789", out _, out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void AMissingIdSegmentIsRejected()
    {
        Assert.False(DiscordWebhook.TryParse(
            "https://discord.com/api/webhooks/", out _, out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void AWrongShapedPathIsRejected()
    {
        // Some other Discord API path -- accepting this would turn the field
        // into a general "any discord.com URL" primitive, not a webhook setting.
        Assert.False(DiscordWebhook.TryParse(
            "https://discord.com/api/channels/123456789/messages", out _, out var error));
        Assert.NotEqual("", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankInputIsRejectedWithoutThrowing(string? raw)
    {
        Assert.False(DiscordWebhook.TryParse(raw, out _, out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void SomethingThatIsNotAUrlAtAllIsRejected()
    {
        Assert.False(DiscordWebhook.TryParse("not a url", out _, out var error));
        Assert.NotEqual("", error);
    }

    // ---- DiscordWebhook.Describe / Redact ----

    private static readonly Uri SampleWebhook = new(
        "https://discord.com/api/webhooks/123456789/abcDEF-token_123");

    [Fact]
    public void DescribeNamesTheHostAndIdButNeverTheToken()
    {
        var description = DiscordWebhook.Describe(SampleWebhook);

        Assert.Contains("discord.com", description);
        Assert.Contains("123456789", description);
        Assert.DoesNotContain("abcDEF-token_123", description);
    }

    [Fact]
    public void RedactScrubsTheFullUrlFromAnArbitraryMessage()
    {
        var message = $"Could not reach {SampleWebhook.AbsoluteUri}: connection refused";

        var redacted = DiscordWebhook.Redact(message, SampleWebhook);

        Assert.DoesNotContain("abcDEF-token_123", redacted);
        Assert.Contains("connection refused", redacted);
    }

    [Fact]
    public void RedactScrubsALoneTokenEvenWithoutTheFullUrl()
    {
        // HttpRequestException messages vary by platform and .NET version --
        // some carry the full request URI, some just a fragment of it. The
        // token itself must never survive either shape.
        var message = "PostAsync failed for token abcDEF-token_123 after 3 retries";

        var redacted = DiscordWebhook.Redact(message, SampleWebhook);

        Assert.DoesNotContain("abcDEF-token_123", redacted);
        Assert.Contains("after 3 retries", redacted);
    }

    [Fact]
    public void RedactLeavesUnrelatedTextAlone()
    {
        var redacted = DiscordWebhook.Redact("Discord returned 500 Internal Server Error", SampleWebhook);

        Assert.Equal("Discord returned 500 Internal Server Error", redacted);
    }
}

/// <summary>
/// A stub transport. Real sockets are never touched: the framing, status
/// mapping and redaction guarantees are all specified against a handler that
/// hands back exactly the response each test needs.
/// </summary>
public sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastMultipartBody { get; private set; }

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content != null)
        {
            LastMultipartBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return _respond(request);
    }
}

public class CombatLogUploadTransportTests : IDisposable
{
    private static readonly Uri SampleWebhook = new(
        "https://discord.com/api/webhooks/123456789/abcDEF-token_123");

    private readonly string _zipPath = Path.Combine(
        Path.GetTempPath(), $"triffview-upload-test-{Guid.NewGuid():N}.zip");

    public CombatLogUploadTransportTests()
    {
        // Content does not matter to CombatLogUpload -- it streams whatever is
        // on disk -- but ZipBytes is read from the real file, so the bytes
        // have to exist.
        File.WriteAllBytes(_zipPath, new byte[] { 1, 2, 3, 4, 5 });
    }

    public void Dispose()
    {
        try { File.Delete(_zipPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static HttpClient ClientReturning(Func<HttpRequestMessage, HttpResponseMessage> respond, out StubHandler handler)
    {
        handler = new StubHandler(respond);
        return new HttpClient(handler);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task ASuccessfulPostReportsSuccessAndTheZipSizeOnDisk(HttpStatusCode status)
    {
        using var http = ClientReturning(_ => new HttpResponseMessage(status), out _);

        var result = await CombatLogUpload.UploadAsync(
            http, SampleWebhook, _zipPath, "content", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(5, result.ZipBytes);
    }

    [Fact]
    public async Task TheMultipartBodyCarriesThePayloadJsonAndTheZipUnderItsFileName()
    {
        using var http = ClientReturning(
            _ => new HttpResponseMessage(HttpStatusCode.OK), out var handler);

        await CombatLogUpload.UploadAsync(
            http, SampleWebhook, _zipPath, "3 pilots, 12:00-12:05Z", CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.IsType<MultipartFormDataContent>(handler.LastRequest!.Content);

        var multipart = (MultipartFormDataContent)handler.LastRequest.Content!;
        var names = multipart.Select(part => part.Headers.ContentDisposition?.Name?.Trim('"')).ToArray();
        Assert.Contains("payload_json", names);
        Assert.Contains("files[0]", names);

        var filePart = multipart.Single(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "files[0]");
        Assert.Equal(
            Path.GetFileName(_zipPath),
            filePart.Headers.ContentDisposition!.FileName!.Trim('"'));

        var payloadPart = multipart.Single(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "payload_json");
        var payloadJson = await payloadPart.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(payloadJson);
        Assert.Equal("3 pilots, 12:00-12:05Z", doc.RootElement.GetProperty("content").GetString());
    }
}
