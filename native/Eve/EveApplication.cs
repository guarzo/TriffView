using System.Reflection;

namespace TriffView.Eve;

/// <summary>
/// Single source of truth for the EVE SSO application identity. TriffFleets and TriffSkills
/// previously each carried their own copy of this (client ID, redirect URI, user agent), which
/// let them drift out of sync with each other and with the app's own version.
/// </summary>
internal static class EveApplication
{
    private const string DefaultClientId = "645ab700da8d40b2bd3524e573385832";

    // Overridable in DEBUG builds only, so a release build can never accidentally ship a
    // developer's own client ID. Named for the application rather than a subsystem: it was
    // TriffSkills-only before the two subsystems were consolidated onto one identity here.
#if DEBUG
    public static readonly string ClientId = Environment.GetEnvironmentVariable("TRIFFVIEW_EVE_CLIENT_ID")?.Trim() is { Length: > 0 } value
        ? value
        : DefaultClientId;
#else
    public const string ClientId = DefaultClientId;
#endif

    // Shared by both subsystems; the "trifffleets" segment is a historical artifact of which
    // controller registered the loopback listener first, not a scoping boundary.
    public const string RedirectUri = "http://127.0.0.1:51777/trifffleets/callback/";

    public static readonly string UserAgent = BuildUserAgent();

    private static string BuildUserAgent()
    {
        var version = typeof(EveApplication).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        var suffix = string.IsNullOrWhiteSpace(version) ? "unknown" : version;
        return $"TriffView/{suffix} (+https://github.com/guarzo/TriffView)";
    }
}
