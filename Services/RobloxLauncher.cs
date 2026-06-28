using System.Diagnostics;
using System.Threading;

namespace RobloxMultiManager.Services;

public enum LauncherKind { Auto, Roblox, Bloxstrap, Fishstrap, Froststrap }

public sealed record LauncherInfo(LauncherKind Kind, string Id, string Name, string? ExePath)
{
    public bool Installed => !string.IsNullOrEmpty(ExePath) && File.Exists(ExePath);
}

public sealed class RobloxLauncher : IDisposable
{
    private static readonly string[] SingletonNames =
    {
        "ROBLOX_singletonEvent",
        "ROBLOX_singletonMutex",
    };

    private const string PlaceLauncherBase =
        "https://assetgame.roblox.com/game/PlaceLauncher.ashx";

    private readonly RobloxWebApi _api;
    private readonly object _lock = new();
    private readonly Dictionary<string, Mutex> _held = new();

    public bool MultiInstanceActive { get; private set; }

    public RobloxLauncher(RobloxWebApi api) => _api = api;

    // ---- launcher selection ----

    public LauncherKind Preferred
    {
        get => _preferred;
        set { _preferred = value; _resolvedValid = false; _resolvedExe = null; }
    }
    private LauncherKind _preferred = LauncherKind.Auto;

    private string? _resolvedExe;
    private bool _resolvedValid;

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private string? ResolvePreferredExe()
    {
        // re-resolve if uncached, or the cached exe vanished (e.g. Roblox updated to a new version
        // folder and deleted the old one) — otherwise we'd fall back to the system handler, which
        // right after an update is the installer/bootstrapper and only opens a single client.
        if (_resolvedValid && (_resolvedExe is null || File.Exists(_resolvedExe))) return _resolvedExe;
        _resolvedExe = _preferred switch
        {
            LauncherKind.Roblox     => FindRobloxPlayer(LocalAppData),
            LauncherKind.Bloxstrap  => FindStrap(LocalAppData, "Bloxstrap"),
            LauncherKind.Fishstrap  => FindStrap(LocalAppData, "Fishstrap"),
            LauncherKind.Froststrap => FindStrap(LocalAppData, "Froststrap"),
            _                       => null,
        };
        _resolvedValid = true;
        return _resolvedExe;
    }

    public bool PreferredMissing => _preferred != LauncherKind.Auto && string.IsNullOrEmpty(ResolvePreferredExe());

    public bool UsesBootstrapper =>
        Preferred is LauncherKind.Bloxstrap or LauncherKind.Fishstrap or LauncherKind.Froststrap;

    public static LauncherKind ParseKind(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "roblox" => LauncherKind.Roblox,
        "bloxstrap" => LauncherKind.Bloxstrap,
        "fishstrap" => LauncherKind.Fishstrap,
        "froststrap" => LauncherKind.Froststrap,
        _ => LauncherKind.Auto,
    };

    public static IReadOnlyList<LauncherInfo> Detect()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[]
        {
            new LauncherInfo(LauncherKind.Roblox,    "roblox",    "Roblox (official)", FindRobloxPlayer(local)),
            new LauncherInfo(LauncherKind.Bloxstrap, "bloxstrap", "Bloxstrap",         FindStrap(local, "Bloxstrap")),
            new LauncherInfo(LauncherKind.Fishstrap, "fishstrap", "Fishstrap",         FindStrap(local, "Fishstrap")),
            new LauncherInfo(LauncherKind.Froststrap,"froststrap","Froststrap",        FindStrap(local, "Froststrap")),
        };
    }

    public string ActiveLauncherLabel()
    {
        if (_preferred == LauncherKind.Auto) return "system default";
        if (PreferredMissing) return $"{_preferred} (not found — using system default)";
        return _preferred switch
        {
            LauncherKind.Roblox => "Roblox (official)",
            _ => _preferred.ToString(),
        };
    }

    // ---- launcher discovery ----

    private static string? FindStrap(string local, string name)
    {
        string p = Path.Combine(local, name, name + ".exe");
        if (File.Exists(p)) return p;

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{name}");
            if (key?.GetValue("InstallLocation") is string loc && !string.IsNullOrWhiteSpace(loc))
            {
                string rp = Path.Combine(loc, name + ".exe");
                if (File.Exists(rp)) return rp;
            }
        }
        catch { }

        try
        {
            string? handler = RegisteredProtocolExe();
            if (handler is not null &&
                Path.GetFileName(handler).Equals(name + ".exe", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(handler))
                return handler;
        }
        catch { }

        try
        {
            string dir = Path.Combine(local, name);
            if (Directory.Exists(dir))
                return Directory.EnumerateFiles(dir, name + ".exe", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { }
        return null;
    }

    private static string? FindRobloxPlayer(string local)
    {
        try
        {
            string versions = Path.Combine(local, "Roblox", "Versions");
            if (!Directory.Exists(versions)) return null;
            string? best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (string dir in Directory.EnumerateDirectories(versions))
            {
                string exe = Path.Combine(dir, "RobloxPlayerBeta.exe");
                if (!File.Exists(exe)) continue;
                DateTime t = File.GetLastWriteTimeUtc(exe);
                if (t >= bestTime) { bestTime = t; best = exe; }
            }
            return best;
        }
        catch { return null; }
    }

    private static string? RegisteredProtocolExe()
    {
        var roots = new (Microsoft.Win32.RegistryKey root, string sub)[]
        {
            (Microsoft.Win32.Registry.CurrentUser,  @"Software\Classes\roblox-player\shell\open\command"),
            (Microsoft.Win32.Registry.ClassesRoot,  @"roblox-player\shell\open\command"),
        };
        foreach (var (root, sub) in roots)
        {
            try
            {
                using var key = root.OpenSubKey(sub);
                if (key?.GetValue("") is string cmd && !string.IsNullOrWhiteSpace(cmd))
                {
                    string exe = ExtractExe(cmd);
                    if (!string.IsNullOrEmpty(exe)) return exe;
                }
            }
            catch { }
        }
        return null;
    }

    private static string ExtractExe(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command.Substring(1, end - 1) : "";
        }
        int sp = command.IndexOf(' ');
        return sp > 0 ? command.Substring(0, sp) : command;
    }

    // ---- single-instance lock ----

    public string AcquireSingleInstanceLock()
    {
        lock (_lock)
        {
            int createdThisCall = 0;
            var problems = new List<string>();

            foreach (string name in SingletonNames)
            {
                if (_held.ContainsKey(name)) continue;

                try
                {
                    var m = new Mutex(initiallyOwned: true, name, out bool createdNew);
                    if (createdNew)
                    {
                        _held[name] = m;
                        createdThisCall++;
                    }
                    else
                    {
                        m.Dispose();
                        problems.Add($"'{name}' already exists (Roblox may already be open).");
                    }
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    problems.Add($"'{name}' is owned by a running Roblox process.");
                }
                catch (Exception ex)
                {
                    problems.Add($"'{name}': {ex.Message}");
                }
            }

            MultiInstanceActive = _held.Count == SingletonNames.Length;

            if (MultiInstanceActive)
                return createdThisCall > 0
                    ? "Multi-instance enabled (singleton lock held)."
                    : "Multi-instance lock already held.";

            return "Multi-instance NOT enabled — " + string.Join(" ", problems) +
                   " Close ALL Roblox windows, then restart this app before launching.";
        }
    }

    public void ReleaseSingleInstanceLock()
    {
        lock (_lock)
        {
            foreach (var m in _held.Values)
            {
                try { m.Dispose(); } catch { }
            }
            _held.Clear();
            MultiInstanceActive = false;
        }
    }

    // ---- launching ----

    public string BuildLaunchUrl(
        string authTicket,
        long placeId,
        string? jobId = null,
        string? privateServerAccessCode = null,
        string? privateServerLinkCode = null,
        long? browserTrackerId = null)
    {
        long btid = browserTrackerId ?? Random.Shared.NextInt64(100_000_000_000L, 1_000_000_000_000L);

        string placeLauncher;
        if (!string.IsNullOrWhiteSpace(privateServerAccessCode) || !string.IsNullOrWhiteSpace(privateServerLinkCode))
        {
            placeLauncher =
                $"{PlaceLauncherBase}?request=RequestPrivateGame&browserTrackerId={btid}&placeId={placeId}";
            if (!string.IsNullOrWhiteSpace(privateServerAccessCode))
                placeLauncher += $"&accessCode={Uri.EscapeDataString(privateServerAccessCode)}";
            if (!string.IsNullOrWhiteSpace(privateServerLinkCode))
                placeLauncher += $"&linkCode={Uri.EscapeDataString(privateServerLinkCode)}";
        }
        else if (!string.IsNullOrWhiteSpace(jobId))
        {
            placeLauncher =
                $"{PlaceLauncherBase}?request=RequestGameJob&browserTrackerId={btid}" +
                $"&placeId={placeId}&gameId={Uri.EscapeDataString(jobId)}";
        }
        else
        {
            placeLauncher =
                $"{PlaceLauncherBase}?request=RequestGame&placeId={placeId}&browserTrackerId={btid}";
        }

        string encoded = Uri.EscapeDataString(placeLauncher);
        long launchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return
            "roblox-player:1" +
            "+launchmode:play" +
            $"+gameinfo:{authTicket}" +
            $"+launchtime:{launchTime}" +
            $"+placelauncherurl:{encoded}" +
            $"+browsertrackerid:{btid}" +
            "+robloxLocale:en_us" +
            "+gameLocale:en_us" +
            "+channel:" +
            "+LaunchExp:InApp";
    }

    public void LaunchProtocol(string protocolUrl)
    {
        if (!MultiInstanceActive) AcquireSingleInstanceLock();
        try
        {
            string? exe = ResolvePreferredExe();
            if (exe is not null && File.Exists(exe))
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                if (Preferred != LauncherKind.Roblox) psi.ArgumentList.Add("-player");
                psi.ArgumentList.Add(protocolUrl);
                using var _ = Process.Start(psi);
            }
            else
            {
                using var _ = Process.Start(new ProcessStartInfo(protocolUrl) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            throw new RobloxApiException(
                "Couldn't start the Roblox client. Is Roblox installed? " + ex.Message, ex);
        }
    }

    public async Task LaunchForAccountAsync(
        string roblosecurityCookie,
        long placeId,
        string? jobId = null,
        string? privateServerAccessCode = null,
        string? privateServerLinkCode = null,
        CancellationToken ct = default)
    {
        if (!MultiInstanceActive) AcquireSingleInstanceLock();

        string ticket = await _api.GetAuthenticationTicketAsync(roblosecurityCookie, ct)
            .ConfigureAwait(false);
        string url = BuildLaunchUrl(ticket, placeId, jobId, privateServerAccessCode, privateServerLinkCode);
        LaunchProtocol(url);
    }

    public void Dispose() => ReleaseSingleInstanceLock();
}
