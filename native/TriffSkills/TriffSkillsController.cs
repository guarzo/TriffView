using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using TriffView.Shared;        // EsiTransport, EsiResponse<T> (Task 0b)
using TriffView.TriffFleets;   // CredentialStore, TokenResponse - internal, same assembly

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
    private const string UserAgent = "TriffView/1.0 TriffSkills";

    // Pinned to guarzo/canifly@main, the same default CanIFly ships
    // (internal/server/env.go:64-69, path suffix applied at
    // internal/services/skillplans/github_downloader.go:131). Deliberately NOT
    // configurable: a settable remote fetch path inside a signed binary that writes
    // files under %APPDATA% is a security conversation this PR should not start.
    // Do not add a setting, an environment variable, or a message parameter for it.
    private const string PlansContentsUrl =
        "https://api.github.com/repos/guarzo/canifly/contents/plans?ref=main";

    // github.com rejects API requests with no User-Agent ("Request forbidden by
    // administrative rules"). Same product token as this file's own SendEsiAsync
    // wrapper (:115) and TriffFleetsController's equivalent (:1506).
    private const string GitHubUserAgent = "TriffView/1.0 TriffSkills";

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
    // .scratch-tests/TriffSkillsMatrixWireTests.cs keeps its own copy of these exact
    // settings, because this file references System.Windows.Threading and cannot be linked
    // into the WPF-free scratch harness. If PropertyNamingPolicy or WriteIndented changes
    // here, update that copy too - a divergence would let the harness assert against a
    // JSON shape PostState no longer actually produces.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly Dispatcher _dispatcher;
    private readonly Action<object> _postToHud;
    private readonly TriffSkillsState _state;
    private readonly SkillIdCache _skillIds;
    private readonly Dictionary<long, AccessTokenCache> _accessTokens = new();
    private string _lastPostedStateJson = "";
    private bool _authInProgress;
    private bool _refreshInFlight;
    private List<SkillPlan> _plans = new();
    private DateTimeOffset? _plansFetchedUtc;

    public TriffSkillsController(Dispatcher dispatcher, Action<object> postToHud)
    {
        _dispatcher = dispatcher;
        _postToHud = postToHud;
        _state = TriffSkillsState.Load();
        _skillIds = SkillIdCache.Load();
        LoadCachedPlans();
    }

    // The transport, retry policy, and EsiResponse<T> are shared with TriffFleets
    // (native/Shared/EsiTransport.cs, extracted in Task 0b). This wrapper binds the three
    // per-tool arguments - our HttpClient, our serializer options, and our User-Agent
    // product token - so call sites read the same as TriffFleets' do.
    private static Task<EsiResponse<T>> SendEsiAsync<T>(HttpMethod method, string path, string? token, object? body = null)
        => EsiTransport.SendAsync<T>(Http, JsonOptions, UserAgent, method, path, token, body);

    // Resolves one batch of skill names through POST /universe/ids/, the same endpoint
    // TriffFleets already calls for character names (TriffFleetsController.cs:1486).
    // Unauthenticated by design - name resolution needs no token, so it works even for a
    // character whose credential has expired.
    private async Task<IReadOnlyList<SkillsUniverseIdName>> ResolveNamesBatchAsync(IReadOnlyList<string> batch)
    {
        var response = await SendEsiAsync<SkillsUniverseIdsResponse>(HttpMethod.Post, "/universe/ids/", token: null, body: batch);
        response.ThrowIfFailed();

        // Names ESI does not recognise are simply omitted from inventory_types. They stay out
        // of the cache and surface as UnknownSkills on the plan.
        return response.Value?.InventoryTypes ?? new List<SkillsUniverseIdName>();
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
            case "triffskills:refresh-characters":
                _ = RefreshCharactersAsync();
                return true;
            case "triffskills:refresh-plans":
                _ = RefreshPlansAsync();
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
    private static async Task<Uri> ReadCallbackUrlAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellationToken) ?? "";
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new InvalidDataException("Local SSO callback was not a valid HTTP request.");
        }

        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
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
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var authSucceeded = false;
        try
        {
            var state = Base64Url(RandomNumberGenerator.GetBytes(32));
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            listener.Start();

            var authUrl = BuildAuthorizeUrl(state, challenge);
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

            var callbackPath = new Uri(RedirectUri).AbsolutePath;

            // Diverges from TriffFleetsController.StartAuthAsync (:366-372), which
            // awaits AcceptTcpClientAsync exactly once and trusts whatever socket
            // wins the race. A browser routinely opens more than one connection
            // around a redirect (preconnects, an abandoned tab from a prior
            // attempt), and any of those can win the accept ahead of the real
            // callback, misreporting a successful login as a parse failure. Loop
            // until a request actually lands on the callback path and carries
            // `code` or `error`, discarding everything else - all under the one
            // 5-minute budget shared by every accept and read below via `cts`,
            // not 5 minutes per socket.
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                await using var stream = client.GetStream();

                Uri callbackUrl;
                try
                {
                    callbackUrl = await ReadCallbackUrlAsync(stream, cts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Not a well-formed HTTP request - e.g. a preconnect socket
                    // that never sent a request line, or an unrelated local
                    // connection. Discard this candidate and keep waiting.
                    continue;
                }

                var query = ParseQuery(callbackUrl.Query);
                var hasCode = query.ContainsKey("code");
                var hasError = query.ContainsKey("error");
                if (callbackUrl.AbsolutePath != callbackPath || (!hasCode && !hasError))
                {
                    // Some other local connection, or a request to the right port
                    // that isn't the SSO redirect. Discard and keep waiting.
                    continue;
                }

                var error = hasError ? query["error"] : "";
                var code = hasCode ? query["code"] : "";
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

                var character = _state.Upsert(identity.CharacterId);
                character.CharacterName = identity.CharacterName;
                character.Scopes = identity.Scopes.ToList();
                character.AuthenticatedUtc = DateTimeOffset.UtcNow;
                character.Error = "";
                character.NeedsReauth = false;
                _state.SelectedCharacterId = identity.CharacterId;
                _state.Save();
                authSucceeded = true;

                // Diverges from TriffFleetsController.StartAuthAsync (:398-399),
                // which writes the refresh token to Credential Manager before
                // calling _state.Save(). If Save() then threw (disk full, an AV
                // lock, a redirected/OneDrive %APPDATA%), that ordering leaves a
                // live refresh token in Credential Manager with no character row
                // to drive ForgetCharacter against - the "forgotten character
                // still has a live token" defect, reached by a different route.
                // Saving state first means a Save() failure never leaves behind a
                // credential the app has no way to remove.
                CredentialStore.Write(RefreshTokenTarget(identity.CharacterId), token.RefreshToken);
                _accessTokens[identity.CharacterId] = new AccessTokenCache(token.AccessToken, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn - 60)));

                await WriteCallbackHtmlAsync(stream, "TriffSkills authentication complete. You can close this tab and return to TriffView.");
                return;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            PostError("auth", "EVE SSO authentication timed out.");
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
            cts.Cancel();
            listener.Stop();
            PostState(force: true);

            // Fire-and-forget, and deliberately after PostState: the user sees the new
            // character row appear immediately, then sees it fill in. Ordering it before
            // the repost would show an empty matrix until the fetch returned.
            //
            // RefreshCharactersAsync degrades per character and never throws out, so an
            // unobserved task here cannot surface as an unhandled exception. It refreshes
            // every character, not just the new one, which is correct: re-authorizing a
            // character that had expired should also clear the stale rows around it.
            if (authSucceeded)
            {
                _ = RefreshCharactersAsync();
            }
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

    // Refreshes every authenticated character's skills and queue.
    //
    // Every failure below degrades exactly one character: the failed character keeps its
    // previous TrainedLevels/Queue/FetchedUtc untouched and gains an error string, while the
    // others are refreshed normally. Nothing here throws out to the caller.
    private async Task RefreshCharactersAsync()
    {
        // A second request while one is in flight is ignored, not queued. All state mutation
        // happens on the dispatcher thread like the other controllers, so there is exactly one
        // writer and this flag is sufficient - concurrency here is cooperative, not locked.
        if (_refreshInFlight) return;

        _refreshInFlight = true;
        PostState(force: true);
        try
        {
            foreach (var character in _state.Characters.ToArray())
            {
                try
                {
                    await RefreshOneCharacterAsync(character);
                }
                catch (Exception ex)
                {
                    // Deliberately broad, and deliberately here rather than in RefreshOneCharacterAsync
                    // or EsiTransport. RefreshOneCharacterAsync already guards RefreshTokenAsync and
                    // treats a non-success EsiResponse<T> as a per-character failure via
                    // CharacterResponseIsUsable, but neither of those covers a 200 response whose body
                    // doesn't match the expected DTO - schema drift, or a captive-portal/proxy handing
                    // back an HTML page with a 200 - which throws a JsonException straight out of
                    // JsonSerializer.Deserialize inside SendEsiAsync. EsiTransport's catch only shields
                    // transient network exceptions (TriffView.Shared.EsiTransport.IsTransientNetworkException),
                    // not shape mismatches, and it is shared with TriffFleets, so widening it there would
                    // change behavior for that tool too. This method's contract is that no single
                    // character's failure aborts the batch, so this is the backstop that makes that true
                    // regardless of what RefreshOneCharacterAsync throws.
                    character.Error = $"Refresh failed unexpectedly: {ex.Message}";
                    PostError("refresh-characters", $"{character.CharacterName}: {character.Error}");
                }

                _state.Save();
                PostState(force: true);
            }
        }
        finally
        {
            _refreshInFlight = false;
            PostState(force: true);
        }
    }

    private async Task RefreshOneCharacterAsync(TriffSkillsCharacter character)
    {
        string token;
        try
        {
            var response = await RefreshTokenAsync(character.CharacterId);
            token = response.AccessToken;
        }
        catch (Exception ex)
        {
            // Token refresh failed. Flag this character; its last-good record stays visible and
            // is rendered stale by its unchanged FetchedUtc.
            character.NeedsReauth = true;
            character.Error = $"Sign-in expired - re-authenticate this character. {ex.Message}";
            PostError("refresh-characters", $"{character.CharacterName}: {character.Error}");
            return;
        }

        var skills = await SendEsiAsync<CharacterSkillsResponse>(
            HttpMethod.Get, $"/characters/{character.CharacterId}/skills/", token);
        if (!CharacterResponseIsUsable(character, skills)) return;

        var queue = await SendEsiAsync<List<SkillQueueItem>>(
            HttpMethod.Get, $"/characters/{character.CharacterId}/skillqueue/", token);
        if (!CharacterResponseIsUsable(character, queue)) return;

        // Written only once BOTH calls have succeeded, so a character is never left holding
        // fresh skills next to a stale queue.
        character.TrainedLevels = EsiSkillMapper.ToTrainedLevels(skills.Value);
        character.Queue = EsiSkillMapper.ToQueue(queue.Value);
        character.FetchedUtc = DateTimeOffset.UtcNow;
        character.Error = "";
        character.NeedsReauth = false;
    }

    // Returns true when the response can be used. On failure the character's previous record is
    // left entirely alone and the error is surfaced both on the record and via PostError
    // (the pattern at EveSettingsController.cs:1099).
    private bool CharacterResponseIsUsable<T>(TriffSkillsCharacter character, EsiResponse<T> response)
    {
        if (response.IsSuccess) return true;

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            // 403 on a skills endpoint is a scope problem, not a transient one, and is
            // deliberately absent from ShouldRetryEsi's transient list. It surfaces here rather
            // than at refresh time because RefreshTokenAsync performs no scope check
            // (TriffFleetsController.cs:526) - a token minted under a different registration
            // refreshes happily and only fails on the first skills call.
            character.NeedsReauth = true;
            character.Error = $"Re-authenticate this character: the stored token does not carry {Scopes}.";
        }
        else
        {
            // Already carries the X-Esi-Error-Limit-Remain / -Reset / Retry-After values that
            // SendEsiAsync appends to Error.
            character.Error = $"{response.Method} {response.Path} returned {(int)response.StatusCode}: {response.Error}";
        }

        PostError("refresh-characters", $"{character.CharacterName}: {character.Error}");
        return false;
    }

    private void LoadCachedPlans()
    {
        try
        {
            _plans = PlanCache.LoadAll(TriffSkillsPaths.PlansDir).ToList();
            _plansFetchedUtc = Directory.Exists(TriffSkillsPaths.PlansDir) && _plans.Count > 0
                ? new DateTimeOffset(Directory.GetLastWriteTimeUtc(TriffSkillsPaths.PlansDir), TimeSpan.Zero)
                : null;
        }
        catch (Exception ex)
        {
            // Deliberately leaves _plans and _plansFetchedUtc exactly as they were.
            // LoadAll now isolates a bad file per-file (PlanCache.cs), so a throw out of
            // here is a directory-level failure (e.g. Recover's Directory.Move racing an
            // antivirus scan) rather than one bad plan. This method also runs after a
            // successful RefreshPlansAsync has already populated a good in-memory list -
            // discarding that on a later, possibly transient, re-read would throw away
            // plans the user could still use. Surface the error; keep what was loaded.
            PostError("plans", $"Could not read the cached plans: {ex.Message}");
        }
    }

    // Manual refresh only - there is no background poll. Unauthenticated GitHub API
    // requests are capped at 60/hour per IP, shared with everything else on that IP,
    // so this runs when the user asks and at no other time.
    private async Task RefreshPlansAsync()
    {
        if (_refreshInFlight)
        {
            PostError("plans", "A refresh is already in progress.");
            return;
        }

        _refreshInFlight = true;
        PostState(force: true);

        var staging = "";
        try
        {
            var listingJson = await GetGitHubStringAsync(PlansContentsUrl);
            var remoteFiles = PlanCatalog.ParseContentsListing(listingJson);
            if (remoteFiles.Count == 0)
            {
                throw new InvalidDataException(
                    "The GitHub plans directory listed no .txt files. The cached plans were left alone.");
            }

            // Everything downloads into a sibling staging directory first. Any failure
            // below abandons it, so a half-finished refresh can never be what the user
            // is left with.
            staging = PlanCache.BeginStaging(TriffSkillsPaths.PlansDir);
            foreach (var remoteFile in remoteFiles)
            {
                PlanCache.WritePlan(staging, remoteFile.Name, await GetGitHubStringAsync(remoteFile.DownloadUrl));
            }

            PlanCache.Commit(TriffSkillsPaths.PlansDir, staging);
            staging = "";
            LoadCachedPlans();

            // Newly-arrived plans may name skills the cache has never seen. Resolve them
            // now so the matrix does not report them Unknown until the next refresh.
            //
            // This runs AFTER the cache swap has already committed, so its failure is not
            // a plan-refresh failure - the plans are on disk and loaded either way. It gets
            // its own try/catch and its own message so a name-resolution outage cannot
            // report "could not refresh plans" for a refresh that in fact succeeded. The
            // only consequence of failing here is that some plans read Unknown until the
            // next refresh, which is exactly the degradation the matrix is built to show.
            try
            {
                var names = _plans
                    .SelectMany(plan => plan.Requirements.Select(requirement => requirement.SkillName))
                    .Where(name => !_skillIds.Map.ContainsKey(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (names.Count > 0)
                {
                    await _skillIds.ResolveMissingAsync(names, ResolveNamesBatchAsync);
                }
            }
            catch (Exception ex)
            {
                PostError("plans", $"Plans updated, but some skill names could not be resolved: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            // The cache is untouched on every path that reaches here: either the failure
            // happened before BeginStaging, or the staging directory is abandoned below.
            // Name resolution is deliberately outside this scope - see its own catch above.
            PostError("plans", $"Could not refresh plans from GitHub: {ex.Message}");
        }
        finally
        {
            if (!string.IsNullOrEmpty(staging))
            {
                try { PlanCache.Abandon(staging); } catch { }
            }
            _refreshInFlight = false;
            PostState(force: true);
        }
    }

    private static async Task<string> GetGitHubStringAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(GitHubUserAgent);

        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            var rateLimitRemaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
                ? string.Join(",", values)
                : "";
            var suffix = string.IsNullOrEmpty(rateLimitRemaining)
                ? ""
                : $" (GitHub rate limit remaining: {rateLimitRemaining})";
            throw new InvalidOperationException($"GET {url} returned {(int)response.StatusCode}.{suffix}");
        }

        return text;
    }

    private void PostState(bool force = false)
    {
        try
        {
            _state.Normalize();
            var matrix = TriffSkillsMatrix.Build(_state.Characters, _plans, _skillIds.Map);
            var wire = TriffSkillsMatrix.ToWire(matrix);
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
                plans = wire.Plans,
                matrix = wire.Matrix,
                // The UI types this as a string and renders "No plans cached" when it is
                // empty, so emit "" rather than null.
                plansFetchedUtc = _plansFetchedUtc?.ToString("o") ?? "",
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
