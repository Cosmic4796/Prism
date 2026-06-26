using System.Net.Http;
using System.Text.Json;

namespace RobloxMultiManager.Services;

public sealed record CommunityEvent(string Id, string Title, long PlaceId, string Game, string Description, string Start, string Host, string Link, string JobId, string PrivateLink);

// ---- community events feed ----
public sealed class EventsService : IDisposable
{
    // community events feed (the Prism server; self-hosters can point this elsewhere)
    private const string Feed = "https://downloadprism.xyz/api/events";

    private readonly HttpClient _http;

    public EventsService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Prism");
    }

    // Throws on a network/HTTP/parse failure so the UI can say "couldn't load" instead of
    // showing an empty list; a valid-but-empty feed returns an empty list.
    public async Task<List<CommunityEvent>> FetchAsync(CancellationToken ct = default)
    {
        var list = new List<CommunityEvent>();
        using var resp = await _http.GetAsync(Feed, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            long placeId = 0;
            if (e.TryGetProperty("placeId", out var pe))
            {
                if (pe.ValueKind == JsonValueKind.Number) pe.TryGetInt64(out placeId);
                else if (pe.ValueKind == JsonValueKind.String) long.TryParse(pe.GetString(), out placeId);
            }
            string id = S("id"), title = S("title");
            if (id.Length == 0 || title.Length == 0) continue;
            list.Add(new CommunityEvent(id, title, placeId, S("game"), S("description"), S("start"), S("host"), S("link"), S("jobId"), S("privateLink")));
        }
        return list;
    }

    public void Dispose() => _http.Dispose();
}
