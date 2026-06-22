using DiscordRPC;

namespace RobloxMultiManager.Services;

public sealed class RichPresenceService : IDisposable
{
    private const string AppId = "1518041189012603022";
    private const string GitHubUrl = "https://github.com/Cosmic4796/Prism";
    private const string DiscordUrl = "https://discord.gg/CZNm9B8JqY";

    private DiscordRpcClient? _client;
    private readonly Timestamps _since = new(DateTime.UtcNow);
    private string _details = "Managing Roblox accounts";
    private string _state = "In the launcher";
    private bool _enabled;
    private readonly object _lock = new();

    private static readonly DiscordRPC.Button[] PresenceButtons =
    {
        new() { Label = "Download", Url = GitHubUrl },
        new() { Label = "Join Discord", Url = DiscordUrl },
    };

    // ---- lifecycle ----
    public void Start()
    {
        lock (_lock)
        {
            if (_client is not null) return;
            try
            {
                _client = new DiscordRpcClient(AppId);
                _client.Initialize();
                _enabled = true;
                Push();
            }
            catch { _client = null; }
        }
    }

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

    public void SetEnabled(bool on) { if (on) Start(); else Stop(); }

    // ---- presence updates ----
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
        catch { }
    }

    private static string Clamp(string s) => s.Length <= 128 ? s : s[..127] + "…";

    public void Dispose() => Stop();
}
