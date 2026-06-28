using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using RobloxMultiManager.Models;

namespace RobloxMultiManager.Services;

public sealed class RobloxWebApi : IDisposable
{
    private const string AuthTicketUrl = "https://auth.roblox.com/v1/authentication-ticket";
    private const string AuthenticatedUserUrl = "https://users.roblox.com/v1/users/authenticated";
    private const string CookieName = ".ROBLOSECURITY";

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private readonly HttpClient _http;

    private readonly ConcurrentDictionary<string, string> _csrfByCookieHash = new();

    public event Action<string, string>? CookieRotated;

    public RobloxWebApi(HttpClient? http = null)
    {
        if (http is not null)
        {
            _http = http;
        }
        else
        {
            var handler = new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        }
    }

    // ---- cookie validation + user info ----

    // Send with a short backoff on 429 so a burst of status checks doesn't flag good accounts as failed.
    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var req = build();
            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req, ct).ConfigureAwait(false); }
            finally { req.Dispose(); }
            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < 2)
            {
                resp.Dispose();
                await Task.Delay(800 * (attempt + 1), ct).ConfigureAwait(false);
                continue;
            }
            return resp;
        }
    }

    public async Task<RobloxUser?> ValidateCookieAndGetUserAsync(
        string roblosecurityCookie, CancellationToken ct = default)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await SendWithRetryAsync(() =>
            {
                var r = new HttpRequestMessage(HttpMethod.Get, AuthenticatedUserUrl);
                AddCookie(r, roblosecurityCookie);
                return r;
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RobloxApiException("Roblox didn't respond in time (timeout). Try again.");
        }
        catch (Exception ex) when (ex is not RobloxApiException and not OperationCanceledException)
        {
            throw new RobloxApiException($"Couldn't reach Roblox: {ex.Message}", ex);
        }

        using (resp)
        {
            CaptureCookieRotation(roblosecurityCookie, resp);

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                return null;

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                throw new RobloxApiException("Roblox rate-limited the request (HTTP 429). Try again shortly.");

            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new RobloxApiException(
                    $"Roblox returned HTTP {(int)resp.StatusCode} validating the cookie. {Trim(body)}");

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out long id))
                    throw new RobloxApiException("Unexpected Roblox response (missing user id).");
                string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string display = root.TryGetProperty("displayName", out var d)
                    ? d.GetString() ?? name
                    : name;
                return new RobloxUser(id, name, display);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new RobloxApiException("Couldn't parse the Roblox user response.", ex);
            }
        }
    }

    public async Task<string?> GetHeadshotThumbnailUrlAsync(
        long userId, string size = "150x150", CancellationToken ct = default)
    {
        string url =
            $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={userId}" +
            $"&size={size}&format=Png&isCircular=false";
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
                return null;
            var first = data[0];
            string? state = first.TryGetProperty("state", out var s) ? s.GetString() : null;
            if (!string.Equals(state, "Completed", StringComparison.OrdinalIgnoreCase))
                return null;
            return first.TryGetProperty("imageUrl", out var img) ? img.GetString() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public async Task<byte[]?> GetHeadshotPngAsync(
        long userId, string size = "150x150", CancellationToken ct = default)
    {
        string? url = await GetHeadshotThumbnailUrlAsync(userId, size, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(url)) return null;
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // ---- account status ----

    public async Task<long?> GetRobuxAsync(string cookie, long userId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://economy.roblox.com/v1/users/{userId}/currency");
        AddCookie(req, cookie);
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            CaptureCookieRotation(cookie, resp);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("robux", out var r) && r.TryGetInt64(out var v) ? v : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    public async Task<bool?> GetPremiumAsync(string cookie, long userId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://premiumfeatures.roblox.com/v1/users/{userId}/validate-membership");
        AddCookie(req, cookie);
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            CaptureCookieRotation(cookie, resp);
            if (!resp.IsSuccessStatusCode) return null;
            string body = (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
            if (bool.TryParse(body, out var b)) return b;
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    // ---- game discovery ----

    private readonly string _sessionId = Guid.NewGuid().ToString();

    public async Task<IReadOnlyList<RobloxGame>> SearchGamesAsync(string query, CancellationToken ct = default)
    {
        var list = new List<RobloxGame>();
        if (string.IsNullOrWhiteSpace(query)) return list;
        var seen = new HashSet<long>();
        string url = $"https://apis.roblox.com/search-api/omni-search?searchQuery={Uri.EscapeDataString(query)}" +
                     $"&sessionId={_sessionId}&pageType=Game";
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return list;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("searchResults", out var sr) && sr.ValueKind == JsonValueKind.Array)
                foreach (var grp in sr.EnumerateArray())
                    if (grp.TryGetProperty("contents", out var cs) && cs.ValueKind == JsonValueKind.Array)
                        foreach (var g in cs.EnumerateArray())
                        {
                            var rg = ParseGame(g);
                            if (rg is not null && seen.Add(rg.UniverseId)) { list.Add(rg); if (list.Count >= 24) return list; }
                        }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        return list;
    }

    public async Task<IReadOnlyList<(string Title, IReadOnlyList<RobloxGame> Games)>> GetDiscoverSortsAsync(
        CancellationToken ct = default)
    {
        var result = new List<(string, IReadOnlyList<RobloxGame>)>();
        string url = $"https://apis.roblox.com/explore-api/v1/get-sorts?sessionId={_sessionId}&device=computer";
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return result;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("sorts", out var sorts) && sorts.ValueKind == JsonValueKind.Array)
                foreach (var s in sorts.EnumerateArray())
                {
                    if (!(s.TryGetProperty("contentType", out var c) &&
                          string.Equals(c.GetString(), "Games", StringComparison.OrdinalIgnoreCase))) continue;
                    if (!s.TryGetProperty("games", out var gs) || gs.ValueKind != JsonValueKind.Array) continue;
                    string title = s.TryGetProperty("sortDisplayName", out var t) ? t.GetString() ?? "" : "";
                    var games = new List<RobloxGame>();
                    foreach (var g in gs.EnumerateArray()) { var rg = ParseGame(g); if (rg is not null) { games.Add(rg); if (games.Count >= 18) break; } }
                    if (games.Count > 0) result.Add((title, games));
                    if (result.Count >= 4) break;
                }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        return result;
    }

    public async Task<Dictionary<long, string>> GetGameIconsAsync(IEnumerable<long> universeIds, CancellationToken ct = default)
    {
        var ids = universeIds.Distinct().ToList();
        var map = new Dictionary<long, string>();
        for (int i = 0; i < ids.Count; i += 100)
        {
            string csv = string.Join(",", ids.Skip(i).Take(100));
            string url = $"https://thumbnails.roblox.com/v1/games/icons?universeIds={csv}&size=150x150&format=Png&isCircular=false";
            try
            {
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    foreach (var d in data.EnumerateArray())
                        if (d.TryGetProperty("targetId", out var tid) && tid.TryGetInt64(out var t) &&
                            d.TryGetProperty("state", out var st) && (st.GetString() ?? "") == "Completed" &&
                            d.TryGetProperty("imageUrl", out var iu))
                            map[t] = iu.GetString() ?? "";
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return map;
    }

    private static RobloxGame? ParseGame(JsonElement g)
    {
        if (!g.TryGetProperty("universeId", out var u) || !u.TryGetInt64(out var uid) || uid <= 0) return null;
        string name = g.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(name)) return null;
        long pid = g.TryGetProperty("rootPlaceId", out var p) && p.TryGetInt64(out var pv) ? pv : 0;
        int playing = g.TryGetProperty("playerCount", out var pc) && pc.TryGetInt32(out var pcv) ? pcv : 0;
        int up = g.TryGetProperty("totalUpVotes", out var uv) && uv.TryGetInt32(out var uvv) ? uvv : 0;
        int down = g.TryGetProperty("totalDownVotes", out var dv) && dv.TryGetInt32(out var dvv) ? dvv : 0;
        return new RobloxGame(uid, pid, name, playing, up, down);
    }

    // ---- public server list ----

    public async Task<IReadOnlyList<RobloxServer>> GetPublicServersAsync(
        long placeId, CancellationToken ct = default)
    {
        string url =
            $"https://games.roblox.com/v1/games/{placeId}/servers/Public?limit=100&sortOrder=Asc";
        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RobloxApiException("Roblox didn't respond in time (timeout) listing servers. Try again.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RobloxApiException($"Couldn't reach Roblox to list servers: {ex.Message}", ex);
        }

        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                throw new RobloxApiException("Roblox rate-limited the server list (HTTP 429). Try again shortly.");

            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new RobloxApiException(
                    $"Roblox returned HTTP {(int)resp.StatusCode} listing servers. {Trim(body)}");

            var list = new List<RobloxServer>();
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in data.EnumerateArray())
                    {
                        string? jobId = s.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                        if (string.IsNullOrEmpty(jobId)) continue;
                        int playing = s.TryGetProperty("playing", out var p) && p.TryGetInt32(out var pv) ? pv : 0;
                        int max = s.TryGetProperty("maxPlayers", out var m) && m.TryGetInt32(out var mv) ? mv : 0;
                        list.Add(new RobloxServer(jobId, playing, max));
                    }
                }
            }
            catch (JsonException ex)
            {
                throw new RobloxApiException("Couldn't parse the Roblox server list.", ex);
            }

            return list.OrderByDescending(s => s.FreeSlots).ToList();
        }
    }

    public async Task<Dictionary<long, string>> GetPlaceNamesAsync(
        string roblosecurityCookie, IEnumerable<long> placeIds, CancellationToken ct = default)
    {
        var ids = placeIds.Distinct().Take(20).ToList();
        var result = new Dictionary<long, string>();
        if (ids.Count == 0) return result;

        string url = "https://games.roblox.com/v1/games/multiget-place-details?" +
                     string.Join("&", ids.Select(i => "placeIds=" + i));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddCookie(req, roblosecurityCookie);
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            CaptureCookieRotation(roblosecurityCookie, resp);
            if (!resp.IsSuccessStatusCode) return result;
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var el in doc.RootElement.EnumerateArray())
                    if (el.TryGetProperty("placeId", out var pid) && pid.TryGetInt64(out var p) &&
                        el.TryGetProperty("name", out var nm))
                        result[p] = nm.GetString() ?? "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        return result;
    }

    // ---- private server access code ----

    public async Task<string?> ResolvePrivateServerAccessCodeAsync(
        string roblosecurityCookie, long placeId, string linkCode, CancellationToken ct = default)
    {
        string url = $"https://www.roblox.com/games/{placeId}?privateServerLinkCode={Uri.EscapeDataString(linkCode)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddCookie(req, roblosecurityCookie);
        req.Headers.TryAddWithoutValidation("Accept", "text/html");
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            CaptureCookieRotation(roblosecurityCookie, resp);
            if (!resp.IsSuccessStatusCode) return null;
            string html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var m = System.Text.RegularExpressions.Regex.Match(
                html, @"joinPrivateGame\(\s*\d+\s*,\s*'([\w\-]+)'");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public async Task<ShareLinkResult?> ResolveShareLinkAsync(
        string roblosecurityCookie, string code, CancellationToken ct = default)
    {
        const string url = "https://apis.roblox.com/sharelinks/v1/resolve-link";
        string body = JsonSerializer.Serialize(new { linkId = code, linkType = "Server" });

        string key = CookieKey(roblosecurityCookie);
        string? csrf = _csrfByCookieHash.TryGetValue(key, out var cached)
            ? cached
            : await PrimeCsrfTokenAsync(roblosecurityCookie, ct).ConfigureAwait(false);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            AddCookie(req, roblosecurityCookie);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com/");
            req.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
            if (!string.IsNullOrEmpty(csrf))
                req.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);

            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new RobloxApiException("Roblox didn't respond in time resolving the share link. Try again."); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { throw new RobloxApiException($"Couldn't reach Roblox to resolve the share link: {ex.Message}", ex); }

            using (resp)
            {
                CaptureCookieRotation(roblosecurityCookie, resp);

                if (resp.StatusCode == HttpStatusCode.Forbidden &&
                    TryReadHeader(resp, "x-csrf-token", out var fresh))
                {
                    csrf = fresh; _csrfByCookieHash[key] = fresh; continue;
                }
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    throw new RobloxApiException("That account's cookie is invalid or expired — re-add it.");
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                    throw new RobloxApiException("Roblox rate-limited the share-link lookup (HTTP 429). Try again shortly.");

                string respBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new RobloxApiException(
                        $"Roblox returned HTTP {(int)resp.StatusCode} resolving the share link. {Trim(respBody)}");

                try
                {
                    using var doc = JsonDocument.Parse(respBody);
                    var root = doc.RootElement;
                    long placeId = FindLong(root, "placeId") ?? 0;
                    string? linkCode = FindString(root, "linkCode");
                    string? accessCode = FindString(root, "accessCode")
                        ?? FindString(root, "privateServerAccessCode")
                        ?? FindString(root, "reservedServerAccessCode");
                    string? gameInstanceId = FindString(root, "gameInstanceId") ?? FindString(root, "gameId");
                    long? privateServerId = FindLong(root, "privateServerId");
                    if (placeId <= 0 && linkCode is null && accessCode is null && gameInstanceId is null)
                        return null;
                    return new ShareLinkResult(placeId, linkCode, accessCode, gameInstanceId, privateServerId);
                }
                catch (JsonException ex)
                {
                    throw new RobloxApiException("Couldn't parse the Roblox share-link response.", ex);
                }
            }
        }
        return null;
    }

    // ---- auth ticket ----

    public async Task<string> GetAuthenticationTicketAsync(
        string roblosecurityCookie, CancellationToken ct = default)
    {
        string key = CookieKey(roblosecurityCookie);
        string? csrf = _csrfByCookieHash.TryGetValue(key, out var cached)
            ? cached
            : await PrimeCsrfTokenAsync(roblosecurityCookie, ct).ConfigureAwait(false);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var resp = await PostAuthTicketAsync(roblosecurityCookie, csrf, ct)
                .ConfigureAwait(false);
            CaptureCookieRotation(roblosecurityCookie, resp);

            if (resp.StatusCode == HttpStatusCode.Forbidden &&
                TryReadHeader(resp, "x-csrf-token", out var fresh))
            {
                csrf = fresh;
                _csrfByCookieHash[key] = fresh;
                continue;
            }

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new RobloxApiException("That account's cookie is invalid or expired — re-add it.");

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                throw new RobloxApiException("Roblox rate-limited the launch (HTTP 429). Wait a moment and retry.");

            if (resp.IsSuccessStatusCode &&
                TryReadHeader(resp, "rbx-authentication-ticket", out var ticket) &&
                !string.IsNullOrWhiteSpace(ticket))
            {
                return ticket;
            }

            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new RobloxApiException(
                $"Couldn't get an authentication ticket (HTTP {(int)resp.StatusCode}). {Trim(body)}");
        }

        throw new RobloxApiException("Couldn't get an authentication ticket after refreshing the CSRF token.");
    }

    private async Task<string> PrimeCsrfTokenAsync(string cookie, CancellationToken ct)
    {
        using var resp = await PostAuthTicketAsync(cookie, csrf: null, ct).ConfigureAwait(false);
        CaptureCookieRotation(cookie, resp);
        if (TryReadHeader(resp, "x-csrf-token", out var token) && !string.IsNullOrEmpty(token))
        {
            _csrfByCookieHash[CookieKey(cookie)] = token;
            return token;
        }
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new RobloxApiException("That account's cookie is invalid or expired — re-add it.");
        return "";
    }

    private async Task<HttpResponseMessage> PostAuthTicketAsync(
        string cookie, string? csrf, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, AuthTicketUrl)
        {
            Content = new StringContent("", Encoding.UTF8, "application/json"),
        };
        AddCookie(req, cookie);
        req.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com/");
        req.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
        req.Headers.TryAddWithoutValidation("RBXAuthenticationNegotiation", "1");
        if (!string.IsNullOrEmpty(csrf))
            req.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);

        try
        {
            return await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RobloxApiException("Roblox auth didn't respond in time (timeout). Try again.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RobloxApiException($"Couldn't reach Roblox auth: {ex.Message}", ex);
        }
    }

    // ---- helpers ----

    private static void AddCookie(HttpRequestMessage req, string roblosecurityCookie)
    {
        string host = req.RequestUri?.Host ?? "";
        if (!(host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase) ||
              host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase)))
            throw new RobloxApiException($"Refusing to send account credentials to non-Roblox host '{host}'.");

        req.Headers.TryAddWithoutValidation("Cookie", $"{CookieName}={roblosecurityCookie}");
    }

    private static string CookieKey(string cookie)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(cookie));
        return Convert.ToHexString(hash);
    }

    private static bool TryReadHeader(HttpResponseMessage resp, string name, out string value)
    {
        if (resp.Headers.TryGetValues(name, out var vals))
        {
            value = vals.FirstOrDefault() ?? "";
            return value.Length > 0;
        }
        value = "";
        return false;
    }

    private void CaptureCookieRotation(string sentCookie, HttpResponseMessage resp)
    {
        // Only trust a rotated cookie from a successful response. A 401/403/429 (expired, CSRF
        // challenge, rate-limit) can carry a Set-Cookie that is NOT a valid replacement; persisting
        // it over the account's good cookie would brick the account and force a re-add.
        if (!resp.IsSuccessStatusCode) return;
        if (!resp.Headers.TryGetValues("Set-Cookie", out var setCookies)) return;

        foreach (string sc in setCookies)
        {
            int eq = sc.IndexOf('=');
            if (eq <= 0) continue;
            string cookieName = sc.AsSpan(0, eq).Trim().ToString();
            if (!cookieName.Equals(CookieName, StringComparison.OrdinalIgnoreCase)) continue;

            int semi = sc.IndexOf(';', eq + 1);
            string newValue = (semi < 0 ? sc[(eq + 1)..] : sc[(eq + 1)..semi]).Trim();

            if (newValue.Length < 16) continue;
            if (newValue == sentCookie) continue;

            try { CookieRotated?.Invoke(sentCookie, newValue); }
            catch { }
            return;
        }
    }

    private static string Trim(string body) =>
        string.IsNullOrWhiteSpace(body) ? "" :
        body.Length <= 200 ? body : body[..200] + "…";

    private static JsonElement? FindProp(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    return prop.Value;
            foreach (var prop in el.EnumerateObject())
                if (FindProp(prop.Value, name) is { } hit) return hit;
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                if (FindProp(item, name) is { } hit) return hit;
        }
        return null;
    }

    private static long? FindLong(JsonElement root, string name)
    {
        if (FindProp(root, name) is not { } v) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    private static string? FindString(JsonElement root, string name)
    {
        if (FindProp(root, name) is not { } v) return null;
        if (v.ValueKind == JsonValueKind.String) { var s = v.GetString(); return string.IsNullOrEmpty(s) ? null : s; }
        if (v.ValueKind == JsonValueKind.Number) return v.ToString();
        return null;
    }

    public void Dispose() => _http.Dispose();
}

public sealed record ShareLinkResult(long PlaceId, string? LinkCode, string? AccessCode, string? JobId, long? PrivateServerId);
