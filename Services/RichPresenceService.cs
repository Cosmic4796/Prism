using DiscordRPC;

namespace RobloxMultiManager.Services;

/// <summary>
/// Optional Discord Rich Presence: shows "Playing Prism" on the user's Discord profile while
/// the app is open — logo, two text lines, an elapsed timer from launch, and Download + Join
/// buttons. Pure free advertising. Entirely best-effort: it no-ops when Discord isn't running
/// (or the user disabled it) and never throws into the app.
/// </summary>
public sealed class RichPresenceService : IDisposable
{
    // The Prism Discord application (same app id as the bot). Its NAME is what renders as
    // "Playing <name>", so the app must be named "Prism" in the Developer Portal, and a square
    // art asset keyed "prism" must be uploaded under Rich Presence → Art Assets.
    private const string AppId = "1518041189012603022";
    private const string GitHubUrl = "https://github.com/Cosmic4796/Prism";
    private const string DiscordUrl = "https://discord.gg/CZNm9B8JqY";

    private DiscordRpcClient? _client;
    private readonly Timestamps _since = new(DateTime.UtcNow); // one instance → stable elapsed timer
    private string _details = "Managing Roblox accounts";
    private string _state = "In the launcher";
    private bool _enabled;
    private readonly object _lock = new();

    private static readonly DiscordRPC.Button[] PresenceButtons =
    {
        new() { Label = "Download", Url = GitHubUrl },
        new() { Label = "Join Discord", Url = DiscordUrl },
    };

    /// <summary>Connect to the local Discord client and show presence. Safe to call repeatedly.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_client is not null) return;
            try
            {
                _client = new DiscordRpcClient(AppId);
                _client.Initialize();      // connects on a background thread; harmless if Discord is closed
                _enabled = true;
                Push();
            }
            catch { _client = null; }
        }
    }

    /// <summary>Clear and disconnect presence.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _enabled = false;
            try { _client?.ClearPresence(); } catch { }
            try { _client?.Dispose(); } catch { }
            _client = null;
        }
    }

    /// <summary>Toggle from a Settings switch.</summary>
    public void SetEnabled(bool on) { if (on) Start(); else Stop(); }

    /// <summary>Set the two visible lines (top = details, bottom = state). Empty values are ignored.</summary>
    public void Update(string? details, string? state)
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(details)) _details = Clamp(details!);
            if (!string.IsNullOrWhiteSpace(state)) _state = Clamp(state!);
            Push();
        }
    }

    private void Push()
    {
        if (!_enabled || _client is null) return;
        try
        {
            _client.SetPresence(new RichPresence
            {
                Details = _details,
                State = _state,
                Timestamps = _since,
                Assets = new Assets
                {
                    LargeImageKey = "prism",
                    LargeImageText = "Prism — Roblox Account Manager",
                },
                Buttons = PresenceButtons,
            });
        }
        catch { /* Discord closed / pipe dropped — ignore */ }
    }

    // Rich-presence text fields max out at 128 chars.
    private static string Clamp(string s) => s.Length <= 128 ? s : s[..127] + "…";

    public void Dispose() => Stop();
}
