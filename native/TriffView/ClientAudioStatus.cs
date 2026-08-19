namespace TriffView.Preview;

/// <summary>
/// Resolves the <c>audioStatus</c> field of one client entry in the <c>triffview:state</c>
/// projection. Pulled out of <c>TriffViewSubsystem.PostState</c> into its own file so it can be
/// exercised directly, without standing up a <c>TriffViewController</c> - everything else in that
/// file is effectively untestable as-is (see CLAUDE.md's testing section).
/// </summary>
internal static class ClientAudioStatus
{
    /// <summary>What a client with no <see cref="TriffView.Audio.TriffAudioService"/> session gets -
    /// splash detection off, or the client simply not yet known to the audio service.</summary>
    public const string Unknown = "off";

    public static string Resolve(IReadOnlyDictionary<uint, string> statusesByProcessId, uint processId)
        => statusesByProcessId.TryGetValue(processId, out var status) ? status : Unknown;
}
