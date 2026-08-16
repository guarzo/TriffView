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
}
