using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using TriffView.TriffFleets;

namespace TriffView.TriffSkills;

internal sealed class TriffSkillsController
{
    // TriffSkills carries its own SSO registration constants rather than reusing
    // TriffFleetsController's. The client ID names the *application* to CCP, so the
    // two tools must be able to point at different registrations (design D2), and
    // port 51778 deliberately differs from TriffFleets' 51777
    // (TriffFleetsController.cs:22) so the loopback listeners cannot collide.
    //
    // Unlike TriffFleets, which hardcodes its client ID (TriffFleetsController.cs:21),
    // TriffSkills resolves the effective value at runtime so a maintainer running his
    // own EVE application does not have to edit and rebuild. The const below is only
    // the fallback. See ResolveClientId().
    private const string DefaultClientId = "REPLACE_WITH_OUR_DEV_REGISTRATION";
    private const string ClientIdEnvVar = "TRIFFVIEW_TRIFFSKILLS_CLIENT_ID";
    private const string RedirectUri = "http://127.0.0.1:51778/triffskills/callback/";
    private const string AuthorizeEndpoint = "https://login.eveonline.com/v2/oauth/authorize";
    private const string TokenEndpoint = "https://login.eveonline.com/v2/oauth/token";
    private const string Scopes = "esi-skills.read_skills.v1 esi-skills.read_skillqueue.v1";

    // Resolution order, first non-empty wins:
    //   1. %APPDATA%\TriffHud\TriffSkills\client-id.txt   (trimmed; a deliberate,
    //      discoverable file next to state.json, for a maintainer who does not want
    //      to manage environment variables)
    //   2. TRIFFVIEW_TRIFFSKILLS_CLIENT_ID                (process/user environment)
    //   3. DefaultClientId                                (what ships in the PR)
    //
    // Any failure reading the file falls through to the next source rather than
    // throwing: an unreadable override must not make the tool unusable, and the
    // "auth not configured" path in Step 6 already reports the end state clearly.
    private static string ResolveClientId()
    {
        try
        {
            var path = Path.Combine(TriffSkillsPaths.Root, "client-id.txt");
            if (File.Exists(path))
            {
                var fromFile = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(fromFile)) return fromFile;
            }
        }
        catch
        {
            // Fall through to the environment variable.
        }

        var fromEnv = Environment.GetEnvironmentVariable(ClientIdEnvVar)?.Trim();
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

        return DefaultClientId;
    }

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
    private readonly Dictionary<long, AccessTokenCache> _accessTokens = new();
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
            case "triffskills:auth":
                _ = StartAuthAsync();
                return true;
            case "triffskills:forget-character":
                ForgetCharacter(message?["characterId"]?.GetValue<long>() ?? 0);
                return true;
            default:
                return false;
        }
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static string BuildQuery(Dictionary<string, string> values)
    {
        return string.Join("&", values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        query = query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pieces[0].Replace("+", " "));
            var value = pieces.Length > 1 ? Uri.UnescapeDataString(pieces[1].Replace("+", " ")) : "";
            result[key] = value;
        }
        return result;
    }

    private static string BuildAuthorizeUrl(string state, string challenge)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ResolveClientId(),
            ["scope"] = Scopes,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        return $"{AuthorizeEndpoint}?{BuildQuery(query)}";
    }

    // The listener is a TcpListener, not an HttpListener - HttpListener on Windows
    // needs a URL ACL reservation or elevation, which is exactly why
    // TriffFleetsController speaks HTTP by hand (:436-476). Read only the request
    // line, drain the headers, then write a minimal HTTP response so the browser
    // tab shows a completion message rather than a connection error.
    private static async Task<Uri> ReadCallbackUrlAsync(NetworkStream stream)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync() ?? "";
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new InvalidDataException("Local SSO callback was not a valid HTTP request.");
        }

        while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
        {
            // Drain headers before writing the callback page.
        }

        return new Uri(new Uri(RedirectUri), parts[1]);
    }

    private static async Task WriteCallbackHtmlAsync(NetworkStream stream, string message)
    {
        var html = $"""
        <!doctype html>
        <html>
        <head><meta charset="utf-8"><title>TriffSkills</title></head>
        <body style="margin:0;background:#05070b;color:#d9e2ee;font-family:Segoe UI,Arial,sans-serif;">
          <main style="max-width:520px;margin:80px auto;border:1px solid #303640;background:#090d14;padding:24px;">
            <h1 style="color:#53b6ff;font-size:18px;text-transform:uppercase;">TriffSkills</h1>
            <p>{WebUtility.HtmlEncode(message)}</p>
          </main>
        </body>
        </html>
        """;
        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n"
        );
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
    }

    // Reads the unverified payload of the access token to learn the character ID,
    // name, and granted scopes. The token's signature is not checked here - it came
    // directly from the SSO token endpoint over TLS, which is the same trust
    // boundary TriffFleetsController.DecodeEveJwt (:1712) relies on.
    private static EveJwtIdentity DecodeEveJwt(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) throw new InvalidDataException("EVE SSO returned an invalid access token.");
        var payload = Encoding.UTF8.GetString(DecodeBase64Url(parts[1]));
        var node = JsonNode.Parse(payload)?.AsObject() ?? throw new InvalidDataException("EVE SSO access token payload could not be read.");
        var characterId = 0L;

        foreach (var key in new[] { "character_id", "CharacterID", "characterID", "characterId" })
        {
            if (node[key] == null) continue;
            try
            {
                characterId = node[key]!.GetValue<long>();
                if (characterId > 0) break;
            }
            catch
            {
                var value = node[key]?.GetValue<string>() ?? "";
                long.TryParse(value, out characterId);
                if (characterId > 0) break;
            }
        }

        if (characterId <= 0)
        {
            var sub = node["sub"]?.GetValue<string>() ?? "";
            var numericTail = sub.Split(':', '/', '|').LastOrDefault(part => long.TryParse(part, out _)) ?? "";
            long.TryParse(numericTail, out characterId);
        }

        if (characterId <= 0)
        {
            throw new InvalidDataException("The EVE SSO token did not include a usable character ID.");
        }

        var name = node["name"]?.GetValue<string>() ?? $"Character {characterId}";
        var scopes = new List<string>();
        if (node["scp"] is JsonArray array)
        {
            scopes.AddRange(array.Select(scope => scope?.GetValue<string>() ?? "").Where(scope => !string.IsNullOrWhiteSpace(scope)));
        }
        else if (node["scp"] != null)
        {
            scopes.AddRange((node["scp"]?.GetValue<string>() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        return new EveJwtIdentity(characterId, name, scopes);
    }

    // The distinct "TriffView.TriffSkills." prefix (parallel to
    // TriffFleetsController.RefreshTokenTarget's "TriffView.TriffFleets." prefix,
    // :1698) is the whole point: TriffSkills must never read a TriffFleets token,
    // and so can never grant itself skill access on the back of a fleets grant.
    private static string RefreshTokenTarget(long characterId) => $"TriffView.TriffSkills.RefreshToken.{characterId}";

    private static async Task<TokenResponse> SendTokenRequestAsync(Dictionary<string, string> form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.UserAgent.ParseAdd("TriffView/1.0 TriffSkills");

        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"EVE SSO returned {(int)response.StatusCode}: {text}");
        }

        return JsonSerializer.Deserialize<TokenResponse>(text, JsonOptions)
            ?? throw new InvalidDataException("EVE SSO returned an empty token response.");
    }

    private static async Task<TokenResponse> ExchangeCodeAsync(string code, string verifier)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = ResolveClientId(),
            ["code_verifier"] = verifier,
            ["redirect_uri"] = RedirectUri,
        };
        return await SendTokenRequestAsync(form);
    }

    // Validates nothing about the returned token's scopes - neither does the fleets
    // original at :526-548. That is a real gap, not an oversight to fix here: if the
    // client ID is later repointed at a different registration, a surviving refresh
    // token can mint an access token missing the skill scopes, and the failure
    // surfaces as a 403 on the first skills call rather than at refresh time. Task 6
    // owns treating that 403 as "re-authenticate this character." The scope check in
    // StartAuthAsync covers interactive grants only and is not a complete defense.
    private async Task<TokenResponse> RefreshTokenAsync(long characterId)
    {
        var refreshToken = CredentialStore.Read(RefreshTokenTarget(characterId));
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("This character needs to authenticate again.");
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ResolveClientId(),
        };
        var token = await SendTokenRequestAsync(form);
        if (!string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            CredentialStore.Write(RefreshTokenTarget(characterId), token.RefreshToken);
        }
        _accessTokens[characterId] = new AccessTokenCache(token.AccessToken, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn - 60)));
        return token;
    }

    private async Task<string> AccessTokenForAsync(long characterId)
    {
        if (_accessTokens.TryGetValue(characterId, out var cached) && cached.ExpiresUtc > DateTimeOffset.UtcNow.AddSeconds(30))
        {
            return cached.AccessToken;
        }

        var token = await RefreshTokenAsync(characterId);
        return token.AccessToken;
    }

    private async Task StartAuthAsync()
    {
        if (_authInProgress)
        {
            PostError("auth", "Character authentication is already in progress.");
            return;
        }

        var clientId = ResolveClientId();
        if (string.IsNullOrWhiteSpace(clientId) || clientId == "REPLACE_WITH_OUR_DEV_REGISTRATION")
        {
            PostError("auth", $"TriffSkills needs a registered EVE SSO client ID carrying esi-skills.read_skills.v1 and esi-skills.read_skillqueue.v1. Set {ClientIdEnvVar}, or put the ID in {Path.Combine(TriffSkillsPaths.Root, "client-id.txt")}.");
            PostState(force: true);
            return;
        }

        _authInProgress = true;
        PostState(force: true);

        using var listener = new TcpListener(IPAddress.Loopback, 51778);
        try
        {
            var state = Base64Url(RandomNumberGenerator.GetBytes(32));
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            listener.Start();

            var authUrl = BuildAuthorizeUrl(state, challenge);
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

            var contextTask = listener.AcceptTcpClientAsync();
            var completed = await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(5)));
            if (completed != contextTask)
            {
                PostError("auth", "EVE SSO authentication timed out.");
                return;
            }

            using var client = await contextTask;
            await using var stream = client.GetStream();
            var callbackUrl = await ReadCallbackUrlAsync(stream);
            var query = ParseQuery(callbackUrl.Query);
            var error = query.TryGetValue("error", out var errorValue) ? errorValue : "";
            var code = query.TryGetValue("code", out var codeValue) ? codeValue : "";
            var returnedState = query.TryGetValue("state", out var stateValue) ? stateValue : "";

            if (!string.IsNullOrWhiteSpace(error))
            {
                await WriteCallbackHtmlAsync(stream, "TriffSkills authentication was cancelled or denied. You can close this tab.");
                PostError("auth", $"EVE SSO returned: {error}");
                return;
            }

            if (!string.Equals(state, returnedState, StringComparison.Ordinal))
            {
                await WriteCallbackHtmlAsync(stream, "TriffSkills blocked this login because the SSO state did not match. You can close this tab.");
                PostError("auth", "EVE SSO state did not match. Authentication was blocked.");
                return;
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                await WriteCallbackHtmlAsync(stream, "TriffSkills did not receive an authorization code. You can close this tab.");
                PostError("auth", "EVE SSO did not return an authorization code.");
                return;
            }

            var token = await ExchangeCodeAsync(code, verifier);
            var identity = DecodeEveJwt(token.AccessToken);

            if (!identity.Scopes.Contains("esi-skills.read_skills.v1") || !identity.Scopes.Contains("esi-skills.read_skillqueue.v1"))
            {
                throw new InvalidDataException("The selected character did not grant the required skill scopes (esi-skills.read_skills.v1, esi-skills.read_skillqueue.v1).");
            }

            if (string.IsNullOrWhiteSpace(token.RefreshToken))
            {
                throw new InvalidDataException("EVE SSO did not return a refresh token.");
            }

            CredentialStore.Write(RefreshTokenTarget(identity.CharacterId), token.RefreshToken);
            _accessTokens[identity.CharacterId] = new AccessTokenCache(token.AccessToken, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn - 60)));

            var character = _state.Upsert(identity.CharacterId);
            character.CharacterName = identity.CharacterName;
            character.Scopes = identity.Scopes.ToList();
            character.AuthenticatedUtc = DateTimeOffset.UtcNow;
            character.Error = "";
            character.NeedsReauth = false;
            _state.SelectedCharacterId = identity.CharacterId;
            _state.Save();

            await WriteCallbackHtmlAsync(stream, "TriffSkills authentication complete. You can close this tab and return to TriffView.");
        }
        catch (SocketException ex)
        {
            PostError("auth", $"Could not open the local SSO callback listener at {RedirectUri}. {ex.Message}");
        }
        catch (Exception ex)
        {
            PostError("auth", ex.Message);
        }
        finally
        {
            _authInProgress = false;
            listener.Stop();
            PostState(force: true);
        }
    }

    // Modelled on TriffFleetsController.ForgetBoss (:141-159): delete the
    // Credential Manager entry, remove the record, drop the cached access token,
    // fix the selection, save, repost. It additionally drops the cached skills and
    // queue, which travel with the record.
    private void ForgetCharacter(long characterId)
    {
        if (characterId <= 0) return;

        // Deletes only the TriffSkills-prefixed credential. The TriffFleets entry for
        // the same character (TriffView.TriffFleets.RefreshToken.{id}) is a different
        // target name and is deliberately left alone.
        CredentialStore.Delete(RefreshTokenTarget(characterId));
        _state.Characters.RemoveAll(character => character.CharacterId == characterId);
        if (_state.SelectedCharacterId == characterId)
        {
            _state.SelectedCharacterId = _state.Characters.FirstOrDefault()?.CharacterId ?? 0;
        }
        _accessTokens.Remove(characterId);
        _state.Save();
        PostState(force: true);
    }

    private void PostState(bool force = false)
    {
        try
        {
            _state.Normalize();
            var state = new
            {
                type = "triffskills:state",
                authConfigured = ResolveClientId() is { Length: > 0 } id && id != "REPLACE_WITH_OUR_DEV_REGISTRATION",
                requiredScopes = Scopes.Split(' '),
                redirectUri = RedirectUri,
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
                    tokenStored = !string.IsNullOrWhiteSpace(CredentialStore.Read(RefreshTokenTarget(character.CharacterId))),
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
