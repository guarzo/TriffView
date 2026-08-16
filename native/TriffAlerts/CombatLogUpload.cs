using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TriffView.Alerts;

/// <summary>
/// Validates and describes Discord webhook URLs. A webhook URL is a bearer
/// credential shaped like a URL -- anyone holding it can post into that
/// channel -- so every method here treats it that way: the allowlist has no
/// override, and <see cref="Redact"/> exists so the token segment can never
/// reach a log line or an error message.
/// </summary>
public static class DiscordWebhook
{
    /// <summary>
    /// The only hosts Discord serves webhooks from. Deliberately not
    /// extensible at runtime -- see the design doc's reasoning for why an "I
    /// know what I'm doing" override would defeat the point of an allowlist
    /// for an unsigned executable handling account data.
    /// </summary>
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord.com",
        "discordapp.com",
        "ptb.discord.com",
        "canary.discord.com",
    };

    public static bool TryParse(string? raw, out Uri webhook, out string error)
    {
        webhook = null!;
        error = "";

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Enter a Discord webhook URL.";
            return false;
        }

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
        {
            error = "That is not a valid URL.";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "Webhook URL must use https.";
            return false;
        }

        if (!AllowedHosts.Contains(uri.Host))
        {
            error = $"'{uri.Host}' is not a Discord webhook host.";
            return false;
        }

        if (!TryReadIdAndToken(uri, out _, out _))
        {
            error = "That doesn't look like a Discord webhook URL " +
                    "(expected .../api/webhooks/{id}/{token}).";
            return false;
        }

        webhook = uri;
        return true;
    }

    /// <summary>
    /// Splits the path into its webhook id and token, or fails. Shared by
    /// <see cref="TryParse"/>, <see cref="Describe"/> and <see cref="Redact"/>
    /// so the segment layout is defined in exactly one place.
    /// </summary>
    private static bool TryReadIdAndToken(Uri uri, out string id, out string token)
    {
        id = "";
        token = "";

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 4) return false;
        if (!segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)) return false;
        if (!segments[1].Equals("webhooks", StringComparison.OrdinalIgnoreCase)) return false;
        if (segments[2].Length == 0 || segments[3].Length == 0) return false;

        id = segments[2];
        token = segments[3];
        return true;
    }

    /// <summary>
    /// A human-readable name for a webhook that omits its token, for the
    /// state posted back to the web UI ("discord.com/api/webhooks/1234…") and
    /// for anywhere else a webhook needs to be named without handing back the
    /// credential that names it.
    /// </summary>
    public static string Describe(Uri webhook)
    {
        return TryReadIdAndToken(webhook, out var id, out _)
            ? $"{webhook.Host}/api/webhooks/{id}…"
            : webhook.Host;
    }

    /// <summary>
    /// Scrubs the token out of an arbitrary string -- an exception message,
    /// most often. Two passes because the token can surface either way:
    /// <see cref="HttpRequestException"/> messages sometimes carry the whole
    /// request URI and sometimes just a fragment naming the token, depending
    /// on platform and .NET version. This is the one thing standing between a
    /// stray exception and the credential reaching <c>PostError</c>, so it is
    /// tested directly rather than assumed.
    /// </summary>
    public static string Redact(string message, Uri webhook)
    {
        if (string.IsNullOrEmpty(message)) return message;

        var result = message.Replace(webhook.AbsoluteUri, Describe(webhook), StringComparison.Ordinal);

        if (TryReadIdAndToken(webhook, out _, out var token) && token.Length > 0)
        {
            result = result.Replace(token, "…", StringComparison.Ordinal);
        }

        return result;
    }
}

/// <summary>
/// Outcome of one POST to a Discord webhook. <see cref="FileCount"/>,
/// <see cref="StartUtc"/>, <see cref="EndUtc"/>, <see cref="Characters"/> and
/// <see cref="DroppedFileCount"/> are not filled in by
/// <see cref="CombatLogUpload.UploadAsync"/> -- it only ever sees the zip on
/// disk and the HTTP exchange -- and are expected to be copied in by the
/// caller from the <c>CombatLogExportResult</c> that produced the archive
/// before this result is posted to the web UI.
/// </summary>
public sealed class CombatLogUploadResult
{
    public bool Succeeded { get; init; }
    public string Message { get; init; } = "";
    public int FileCount { get; init; }
    public long ZipBytes { get; init; }
    public DateTime StartUtc { get; init; }
    public DateTime EndUtc { get; init; }
    public IReadOnlyList<string> Characters { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Rides through from the export result and, per the design doc, is
    /// reported on a *successful* upload too -- an upload that quietly
    /// dropped files would read as complete coverage to whoever builds an
    /// after-action report from the Discord channel, having never seen this
    /// app's UI.
    /// </summary>
    public int DroppedFileCount { get; init; }

    public object ToState()
    {
        return new
        {
            succeeded = Succeeded,
            message = Message,
            fileCount = FileCount,
            zipBytes = ZipBytes,
            startUtc = StartUtc.ToString("O"),
            endUtc = EndUtc.ToString("O"),
            characters = Characters,
            droppedFileCount = DroppedFileCount,
        };
    }
}

/// <summary>
/// Posts a combat log archive to a Discord webhook. Every path through
/// <see cref="UploadAsync"/> returns a result rather than throwing, for any
/// response the server produced or any transport error it can classify --
/// that single return path is what makes it possible to guarantee every
/// outbound string has been through <see cref="DiscordWebhook.Redact"/>.
/// An exception escaping to the subsystem's generic catch would bypass that
/// and reach the UI unredacted.
/// </summary>
public static class CombatLogUpload
{
    /// <summary>
    /// The HttpClient instances elsewhere in this repo are tuned for small
    /// JSON calls to ESI (8-20s) and would be wrong for pushing up to 10MB
    /// over a domestic connection. Callers are expected to bound their own
    /// CancellationToken to this many seconds rather than lower the timeout
    /// on a shared client.
    /// </summary>
    public const int UploadTimeoutSeconds = 120;

    public static async Task<CombatLogUploadResult> UploadAsync(
        HttpClient http, Uri webhook, string zipPath, string content, CancellationToken ct)
    {
        var zipBytes = new FileInfo(zipPath).Length;

        try
        {
            // Not disposed here: MultipartContent.Dispose() clears its own nested-parts
            // list as well as the streams within it, and a caller (or a test double)
            // that inspects the request's content after this method returns would see
            // an empty multipart. The one resource that actually needs closing --
            // the file handle -- is closed explicitly below instead.
            var form = new MultipartFormDataContent();
            var payloadJson = JsonSerializer.Serialize(new { content });
            form.Add(new StringContent(payloadJson, Encoding.UTF8, "application/json"), "payload_json");

            await using var fileStream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(fileContent, "files[0]", Path.GetFileName(zipPath));

            using var response = await http.PostAsync(webhook, form, ct);
            return await BuildResultAsync(response, zipBytes, webhook, ct);
        }
        catch (OperationCanceledException)
        {
            // Covers both HttpClient's own internal timeout and a caller-supplied
            // token bounded to UploadTimeoutSeconds -- either way, the caller
            // gets a reportable result instead of an exception reaching the
            // subsystem's generic catch unredacted.
            return Failed("The upload timed out.", zipBytes, webhook);
        }
        catch (HttpRequestException ex)
        {
            return Failed($"Could not reach Discord: {ex.Message}", zipBytes, webhook);
        }
    }

    private static async Task<CombatLogUploadResult> BuildResultAsync(
        HttpResponseMessage response, long zipBytes, Uri webhook, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent)
        {
            return new CombatLogUploadResult
            {
                Succeeded = true,
                Message = "Uploaded to Discord.",
                ZipBytes = zipBytes,
            };
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return Failed("The webhook no longer exists or was deleted in Discord.", zipBytes, webhook);
        }

        if ((int)response.StatusCode == 413)
        {
            return Failed("The archive is too large for that server.", zipBytes, webhook);
        }

        if ((int)response.StatusCode == 429)
        {
            var retryAfter = await TryReadRetryAfterSecondsAsync(response, ct);
            var suffix = retryAfter is { } seconds
                ? $" Retry after {seconds.ToString("0.#", CultureInfo.InvariantCulture)}s."
                : "";
            return Failed($"Rate limited by Discord.{suffix}", zipBytes, webhook);
        }

        return Failed($"Discord returned {(int)response.StatusCode} {response.ReasonPhrase}.", zipBytes, webhook);
    }

    /// <summary>
    /// Discord's JSON body carries sub-second precision; the Retry-After
    /// header, when present at all, is whole seconds. The body wins when both
    /// are there.
    /// </summary>
    private static async Task<double?> TryReadRetryAfterSecondsAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("retry_after", out var value) && value.ValueKind == JsonValueKind.Number)
            {
                return value.GetDouble();
            }
        }
        catch (JsonException)
        {
        }

        return response.Headers.RetryAfter?.Delta?.TotalSeconds;
    }

    private static CombatLogUploadResult Failed(string message, long zipBytes, Uri webhook)
    {
        return new CombatLogUploadResult
        {
            Succeeded = false,
            // Routed through Redact even for messages that plainly do not
            // contain the token: "every outbound string" means every one,
            // not every one a reviewer remembered to check by hand.
            Message = DiscordWebhook.Redact(message, webhook),
            ZipBytes = zipBytes,
        };
    }
}
