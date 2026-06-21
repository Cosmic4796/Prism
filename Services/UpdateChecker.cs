using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RobloxMultiManager.Services;

/// <summary>Info about a newer Prism release found on GitHub.</summary>
public sealed record UpdateInfo(string Version, string Notes, string Url);

/// <summary>
/// Checks GitHub Releases for a newer Prism build. Completely independent of Roblox —
/// no cookies, its own HttpClient. Best-effort: never throws (returns null on failure).
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    private const string LatestApi = "https://api.github.com/repos/Cosmic4796/Prism/releases/latest";
    private const string DownloadUrl = "https://github.com/Cosmic4796/Prism/releases/latest/download/Prism.exe";

    private readonly HttpClient _http;

    public UpdateChecker()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub's API rejects requests without a User-Agent.
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Prism-UpdateChecker");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
    }

    /// <summary>Returns update info if the latest release is newer than <paramref name="current"/>, else null.</summary>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(LatestApi, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

            var latest = ParseVersion(tag);
            if (latest is null || latest <= Normalize(current)) return null;

            return new UpdateInfo(tag.TrimStart('v', 'V').Trim(), Trim(notes), DownloadUrl);
        }
        catch { return null; }
    }

    /// <summary>Reduce a Version to Major.Minor.Build so a 3-part tag never loses to a 4-part assembly version.</summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);

    private static Version? ParseVersion(string tag)
    {
        var m = Regex.Match(tag.TrimStart('v', 'V').Trim(), @"^\d+(?:\.\d+){1,3}");
        return m.Success && Version.TryParse(m.Value, out var v) ? Normalize(v) : null;
    }

    private static string Trim(string s) => s.Length <= 1500 ? s : s[..1500] + "…";

    public void Dispose() => _http.Dispose();
}
