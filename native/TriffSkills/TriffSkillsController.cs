using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;

namespace TriffView.TriffSkills;

internal sealed class TriffSkillsController
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly Dispatcher _dispatcher;
    private readonly Action<object> _postToHud;
    private readonly TriffSkillsState _state;
    private string _lastPostedStateJson = "";
    private bool _authInProgress;
    private bool _refreshInFlight;

    public TriffSkillsController(Dispatcher dispatcher, Action<object> postToHud)
    {
        _dispatcher = dispatcher;
        _postToHud = postToHud;
        _state = TriffSkillsState.Load();
    }

    public bool HandleWebMessage(string type, JsonObject? message)
    {
        switch (type)
        {
            case "triffskills:get-state":
                PostState(force: true);
                return true;
            default:
                return false;
        }
    }

    private void PostState(bool force = false)
    {
        try
        {
            _state.Normalize();
            var state = new
            {
                type = "triffskills:state",
                authInProgress = _authInProgress,
                refreshInFlight = _refreshInFlight,
                selectedCharacterId = _state.SelectedCharacterId,
                characters = _state.Characters.Select(character => new
                {
                    character.CharacterId,
                    character.CharacterName,
                    character.Scopes,
                    character.AuthenticatedUtc,
                    character.FetchedUtc,
                    character.Error,
                    character.NeedsReauth,
                }).ToArray(),
            };
            var json = JsonSerializer.Serialize(state, JsonOptions);
            if (!force && string.Equals(json, _lastPostedStateJson, StringComparison.Ordinal)) return;
            _lastPostedStateJson = json;
            _postToHud(state);
        }
        catch (Exception ex)
        {
            PostError("state", ex.Message);
        }
    }

    private void PostError(string category, string message)
    {
        _postToHud(new
        {
            type = "triffskills:error",
            action = category,
            message,
        });
    }
}
