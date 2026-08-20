using System.Text.Json;

namespace TriffView;

/// <summary>
/// The single serializer configuration for every native -> web message.
///
/// It lives here rather than on MainWindow because the state payload is serialized where it is
/// built and posted somewhere else. When each side owned its own options, the payload was
/// serialized twice per post - once to compare against the previously sent state, once to
/// actually send it - and the two calls did not even have to agree, so the string being compared
/// was not guaranteed to be the string being sent.
/// </summary>
internal static class WebMessageJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
