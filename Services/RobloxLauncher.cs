using System.Diagnostics;
using System.Threading;

namespace RobloxMultiManager.Services;

/// <summary>Which client/bootstrapper Prism hands the launch URI to.</summary>
public enum LauncherKind { Auto, Roblox, Bloxstrap, Fishstrap, Froststrap }

/// <summary>A detected (or known-but-missing) launcher Prism can route through.</summary>
public sealed record LauncherInfo(LauncherKind Kind, string Id, string Name, string? ExePath)
{
    public bool Installed => !string.IsNullOrEmpty(ExePath) && File.Exists(ExePath);
}

/// <summary>
/// Launches Roblox clients and enables running several at once.
///
/// Multi-instance: the Roblox client enforces "only one copy" with a named kernel
/// object (an EVENT "ROBLOX_singletonEvent" on current clients, a MUTEX
/// "ROBLOX_singletonMutex" on older ones). If <i>we</i> create those names first and
/// keep the handles open for our whole lifetime, the client can't take exclusive
/// ownership, so additional clients launch normally. No admin required; the holder
/// just has to live in the same Windows session and stay running.
/// </summary>
public sealed class RobloxLauncher : IDisposable
{
    // Hold BOTH names: current client uses the Event, older clients the Mutex.
    private static readonly string[] SingletonNames =
    {
        "ROBLOX_singletonEvent",
        "ROBLOX_singletonMutex",
    };

    private const string PlaceLauncherBase =
        "https://assetgame.roblox.com/game/PlaceLauncher.ashx";

    private readonly RobloxWebApi _api;
    private readonly object _lock = new();
    private readonly Dictionary<string, Mutex> _held = new(); // singleton name -> our handle

    /// <summary>
    /// True only when WE freshly created BOTH singleton names (so no Roblox owns
    /// either and extra clients will launch). If a client is already running it owns
    /// one of the names and this stays false — that's the honest state.
    /// </summary>
    public bool MultiInstanceActive { get; private set; }

    public RobloxLauncher(RobloxWebApi api) => _api = api;

    // ----- launcher selection (Bloxstrap / Fishstrap / Froststrap / official) -

    /// <summary>
    /// Which launcher to route the protocol URI through. <see cref="LauncherKind.Auto"/>
    /// (default) hands the URI to Windows, which uses whatever is registered as the
    /// <c>roblox-player</c> handler. Any other value invokes that launcher's exe directly
    /// (so we don't depend on Roblox not having reclaimed the protocol on its last update).
    /// Setting it clears the resolved-exe cache so the next launch re-probes.
    /// </summary>
    public LauncherKind Preferred
    {
        get => _preferred;
        set { _preferred = value; _resolvedValid = false; _resolvedExe = null; }
    }
    private LauncherKind _preferred = LauncherKind.Auto;

    // Resolving the chosen exe walks the filesystem, so cache it for the run (a launch batch
    // hits this once per account). Invalidated whenever Preferred changes.
    private string? _resolvedExe;
    private bool _resolvedValid;

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Resolves (and caches) the exe for the current <see cref="Preferred"/> — probing
    /// only that one launcher, not all four. Null for Auto or when the pick isn't installed.</summary>
    private string? ResolvePreferredExe()
    {
        if (_resolvedValid) return _resolvedExe;
        _resolvedExe = _preferred switch
        {
            LauncherKind.Roblox     => FindRobloxPlayer(LocalAppData),
            LauncherKind.Bloxstrap  => FindStrap(LocalAppData, "Bloxstrap"),
            LauncherKind.Fishstrap  => FindStrap(LocalAppData, "Fishstrap"),
            LauncherKind.Froststrap => FindStrap(LocalAppData, "Froststrap"),
            _                       => null, // Auto
        };
        _resolvedValid = true;
        return _resolvedExe;
    }

    /// <summary>True when a specific (non-Auto) launcher is picked but can't be found — so a
    /// launch will quietly fall back to the system default handler. Lets the UI warn loudly.</summary>
    public bool PreferredMissing => _preferred != LauncherKind.Auto && string.IsNullOrEmpty(ResolvePreferredExe());

    /// <summary>
    /// True when a Bloxstrap-family bootstrapper is explicitly selected. Those serialize their
    /// own bootstrap step (an AsyncMutex) and run an update/FastFlag pass before spawning Roblox,
    /// so the caller should space sequential launches more generously than for the bare client.
    /// </summary>
    public bool UsesBootstrapper =>
        Preferred is LauncherKind.Bloxstrap or LauncherKind.Fishstrap or LauncherKind.Froststrap;

    /// <summary>Parses the UI setting string ("froststrap", "bloxstrap", …) to a kind. Defaults to Auto.</summary>
    public static LauncherKind ParseKind(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "roblox" => LauncherKind.Roblox,
        "bloxstrap" => LauncherKind.Bloxstrap,
        "fishstrap" => LauncherKind.Fishstrap,
        "froststrap" => LauncherKind.Froststrap,
        _ => LauncherKind.Auto,
    };

    /// <summary>
    /// Probes the machine for the official Roblox client and the common Bloxstrap forks,
    /// returning each with its resolved exe path (null when not installed). Cheap enough to
    /// call on demand; the UI uses it to show which launchers are available.
    /// </summary>
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

    /// <summary>A short human label for the launcher in effect, noting a graceful fallback.</summary>
    public string ActiveLauncherLabel()
    {
        if (_preferred == LauncherKind.Auto) return "system default";
        if (PreferredMissing) return $"{_preferred} (not found — using system default)";
        return _preferred switch
        {
            LauncherKind.Roblox => "Roblox (official)",
            _ => _preferred.ToString(), // Bloxstrap / Fishstrap / Froststrap
        };
    }

    // Bootstrappers install as %LOCALAPPDATA%\<Name>\<Name>.exe; the launcher exe in the
    // install root is the registered protocol handler. Fall back to a shallow search so a
    // fork that nests its build still resolves.
    private static string? FindStrap(string local, string name)
    {
        string p = Path.Combine(local, name, name + ".exe");
        if (File.Exists(p)) return p;

        // Relocated install: the bootstrapper records where it put itself.
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
        catch { /* registry access denied / missing */ }

        // Authoritative when this strap currently owns the protocol from a non-default location:
        // the registered roblox-player handler command points straight at its exe.
        try
        {
            string? handler = RegisteredProtocolExe();
            if (handler is not null &&
                Path.GetFileName(handler).Equals(name + ".exe", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(handler))
                return handler;
        }
        catch { /* registry access denied / missing */ }

        // Last resort: a fork that nests its build under the default folder.
        try
        {
            string dir = Path.Combine(local, name);
            if (Directory.Exists(dir))
                return Directory.EnumerateFiles(dir, name + ".exe", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { /* permissions / transient IO */ }
        return null;
    }

    // Official client lives at %LOCALAPPDATA%\Roblox\Versions\<hash>\RobloxPlayerBeta.exe;
    // pick the most recently written one (the current channel). The exe sits directly in each
    // version folder, so probe one level deep — not a recursive walk of the whole (huge) tree.
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

    // The exe registered to open roblox-player: links (HKCU classes first, then machine-wide).
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
            catch { /* access denied / missing */ }
        }
        return null;
    }

    // Pull the leading executable path out of a registered shell command (handles quoting).
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

    // ----- single-instance lock ----------------------------------------------

    /// <summary>
    /// Idempotently creates and holds the Roblox singleton kernel objects so extra
    /// clients can launch. Safe to call repeatedly. Returns a short human-readable
    /// status for the UI log.
    /// </summary>
    public string AcquireSingleInstanceLock()
    {
        lock (_lock)
        {
            int createdThisCall = 0;
            var problems = new List<string>();

            foreach (string name in SingletonNames)
            {
                if (_held.ContainsKey(name)) continue; // already holding it; don't duplicate

                try
                {
                    // 3-arg overload so we know whether we actually created it fresh.
                    var m = new Mutex(initiallyOwned: true, name, out bool createdNew);
                    if (createdNew)
                    {
                        _held[name] = m; // keep the handle alive for the app lifetime
                        createdThisCall++;
                    }
                    else
                    {
                        // Someone already owns this name (another instance of us, or
                        // Roblox on older clients). We don't own it; drop our handle.
                        m.Dispose();
                        problems.Add($"'{name}' already exists (Roblox may already be open).");
                    }
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    // A different-typed kernel object with this name exists — i.e. a
                    // running Roblox created it first (current client uses an Event).
                    problems.Add($"'{name}' is owned by a running Roblox process.");
                }
                catch (Exception ex)
                {
                    problems.Add($"'{name}': {ex.Message}");
                }
            }

            // Truly enabled only when we hold every singleton name ourselves.
            MultiInstanceActive = _held.Count == SingletonNames.Length;

            if (MultiInstanceActive)
                return createdThisCall > 0
                    ? "Multi-instance enabled (singleton lock held)."
                    : "Multi-instance lock already held.";

            return "Multi-instance NOT enabled — " + string.Join(" ", problems) +
                   " Close ALL Roblox windows, then restart this app before launching.";
        }
    }

    /// <summary>Releases the held singleton objects. Call only on app shutdown.</summary>
    public void ReleaseSingleInstanceLock()
    {
        lock (_lock)
        {
            foreach (var m in _held.Values)
            {
                try { m.Dispose(); } catch { /* best effort */ }
            }
            _held.Clear();
            MultiInstanceActive = false;
        }
    }

    // ----- launching ----------------------------------------------------------

    /// <summary>
    /// Builds the <c>roblox-player:</c> protocol string. Pick the join type:
    ///   * <paramref name="privateServerAccessCode"/> set ⇒ private/VIP server,
    ///   * else <paramref name="jobId"/> set ⇒ that specific server instance
    ///     (use this to land multiple alts in the SAME server for trading),
    ///   * else public matchmaking (any available server).
    /// </summary>
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
            // Private/VIP server. accessCode is the signed GUID Roblox needs; linkCode
            // lets the server resolve it too. We pass whichever we have (ideally both).
            placeLauncher =
                $"{PlaceLauncherBase}?request=RequestPrivateGame&browserTrackerId={btid}&placeId={placeId}";
            if (!string.IsNullOrWhiteSpace(privateServerAccessCode))
                placeLauncher += $"&accessCode={Uri.EscapeDataString(privateServerAccessCode)}";
            if (!string.IsNullOrWhiteSpace(privateServerLinkCode))
                placeLauncher += $"&linkCode={Uri.EscapeDataString(privateServerLinkCode)}";
        }
        else if (!string.IsNullOrWhiteSpace(jobId))
        {
            // 'gameId' is the query key; its VALUE is the server's JobId GUID.
            placeLauncher =
                $"{PlaceLauncherBase}?request=RequestGameJob&browserTrackerId={btid}" +
                $"&placeId={placeId}&gameId={Uri.EscapeDataString(jobId)}";
        }
        else
        {
            placeLauncher =
                $"{PlaceLauncherBase}?request=RequestGame&placeId={placeId}&browserTrackerId={btid}";
        }

        // Encode the inner URL so its ? & = don't collide with the '+'-delimited pairs.
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

    /// <summary>
    /// Hands a finished protocol URL to the chosen launcher. With <see cref="LauncherKind.Auto"/>
    /// (or when the picked launcher isn't installed) it goes to Windows' registered
    /// <c>roblox-player</c> handler; otherwise we invoke that launcher's exe directly.
    /// Ensures the multi-instance lock is held first.
    /// </summary>
    public void LaunchProtocol(string protocolUrl)
    {
        if (!MultiInstanceActive) AcquireSingleInstanceLock();
        try
        {
            string? exe = ResolvePreferredExe();
            if (exe is not null && File.Exists(exe))
            {
                // Route explicitly through the chosen bootstrapper/client. We hold the singleton
                // lock, so the bootstrapper's multi-instance watcher sees the mutex already taken
                // and defers to us — extra clients still launch. ArgumentList quotes safely.
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                // Bloxstrap & its forks register their protocol handler as `<exe> -player "<uri>"`,
                // so match that exactly. The official RobloxPlayerBeta.exe takes the bare URI as arg[0].
                if (Preferred != LauncherKind.Roblox) psi.ArgumentList.Add("-player");
                psi.ArgumentList.Add(protocolUrl);
                using var _ = Process.Start(psi);
            }
            else
            {
                // Auto, or the picked launcher is missing: let Windows route the URI to the
                // registered handler. Protocol launches don't yield a usable Process handle.
                using var _ = Process.Start(new ProcessStartInfo(protocolUrl) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            throw new RobloxApiException(
                "Couldn't start the Roblox client. Is Roblox installed? " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Mints a fresh ticket for the cookie and launches the client into the chosen
    /// place/server in one step (minimizing the ticket's short expiry window).
    /// </summary>
    public async Task LaunchForAccountAsync(
        string roblosecurityCookie,
        long placeId,
        string? jobId = null,
        string? privateServerAccessCode = null,
        string? privateServerLinkCode = null,
        CancellationToken ct = default)
    {
        // Make sure the lock is held before we spend the ticket — acquiring it after
        // Roblox is already coming up is too late.
        if (!MultiInstanceActive) AcquireSingleInstanceLock();

        string ticket = await _api.GetAuthenticationTicketAsync(roblosecurityCookie, ct)
            .ConfigureAwait(false);
        string url = BuildLaunchUrl(ticket, placeId, jobId, privateServerAccessCode, privateServerLinkCode);
        LaunchProtocol(url);
    }

    public void Dispose() => ReleaseSingleInstanceLock();
}
