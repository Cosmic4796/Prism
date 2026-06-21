using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using RobloxMultiManager.Models;
using RobloxMultiManager.Services;

namespace RobloxMultiManager.UI;

/// <summary>
/// Prism's main window. The entire UI is a web app (UI/web/app.html) rendered in a
/// full-window WebView2; this class is the host + the JSON bridge that connects the
/// web UI's actions to the existing C# services. The web layer is purely presentation —
/// all cookies/launching/encryption stay in the verified C# backend.
/// </summary>
public sealed class AppShell : Form
{
    private readonly AccountManager _accounts;
    private readonly RobloxLauncher _launcher;
    private readonly RobloxWebApi _api;
    private readonly WebView2 _web;
    private readonly Dictionary<long, string> _avatarUrls = new();
    private readonly Dictionary<long, (string health, long? robux, bool? premium)> _acctInfo = new();
    private bool _infoRunning, _autoStarted;
    private string _lockMessage = "";
    private bool _loginOpen;

    // ----- demo capture (env-gated; off in normal use) ------------------------
    // PRISM_DEMO=<frame output dir> turns on a scripted auto-tour + frame capture.
    private readonly string? _demoFrameDir = Environment.GetEnvironmentVariable("PRISM_DEMO");
    private readonly string? _demoScriptPath = Environment.GetEnvironmentVariable("PRISM_DEMO_SCRIPT");
    private bool Demo => !string.IsNullOrEmpty(_demoFrameDir);
    private bool _demoFinishing;
    private int _scFrame;
    private readonly List<string> _scTimes = new();

    private readonly string _settingsPath = Path.Combine(AppData.Dir, "settings.json");
    private Dictionary<string, string> _settings = new();

    private readonly string _statsPath = Path.Combine(AppData.Dir, "stats.json");
    private Stats _stats = new();

    private sealed class Stats
    {
        public int Version { get; set; } = 2; // 1=totals only, 2=adds per-launch History
        public int TotalLaunches { get; set; }
        public Dictionary<string, int> ByPlace { get; set; } = new();
        public Dictionary<string, int> ByAccount { get; set; } = new();
        public long LastPlaceId { get; set; }
        public List<LaunchEntry> History { get; set; } = new(); // recent launches (bounded)
    }

    private sealed class LaunchEntry
    {
        public long PlaceId { get; set; }
        public long UserId { get; set; }
        public string Time { get; set; } = ""; // ISO-8601 UTC
    }

    public AppShell(AccountManager accounts, RobloxLauncher launcher, RobloxWebApi api)
    {
        _accounts = accounts;
        _launcher = launcher;
        _api = api;
        LoadSettings();
        LoadStats();
        _closeToTray = _settings.GetValueOrDefault("closeToTray") == "1";
        _minimizeToTray = _settings.GetValueOrDefault("minimizeToTray") == "1";
        _autoRejoin = _settings.GetValueOrDefault("autoRejoin") == "1";
        _rpEnabled = _settings.GetValueOrDefault("richPresence") != "0"; // on by default
        _launcher.Preferred = RobloxLauncher.ParseKind(_settings.GetValueOrDefault("launcher"));
        ApplyStartup(_settings.GetValueOrDefault("startWithWindows") == "1");

        Text = Theme.AppTitle;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);
        ClientSize = new Size(1100, 730);
        if (Demo) ClientSize = new Size(1280, 720); // clean 16:9 for the demo video
        FormBorderStyle = FormBorderStyle.None; // custom title bar lives in the web UI
        Theme.ApplyForm(this);

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Bg };
        Controls.Add(_web);

        // Tell the web UI when maximize state changes (snap, double-click, drag-restore).
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized) { _wasMin = true; if (_minimizeToTray) Hide(); return; }
            ClipSquare(); // keep the square clip matching the new size
            if (_wasMin) { _wasMin = false; Post(new { @event = "restored" }); } // let the UI animate back in
            bool max = WindowState == FormWindowState.Maximized;
            if (max != _lastMax) { _lastMax = max; PushWinState(); }
        };

        FormClosing += (_, e) => { if (_closeToTray && !_allowExit) { e.Cancel = true; Hide(); } };
        FormClosed += (_, _) => { try { _tray?.Dispose(); } catch { } try { _keepAliveTimer?.Dispose(); } catch { } try { _updater.Dispose(); } catch { } try { _rp.Dispose(); } catch { } };

        Load += async (_, _) => await InitAsync();
    }

    private bool _lastMax;
    private bool _wasMin;
    private NotifyIcon? _tray;
    private bool _closeToTray, _minimizeToTray, _allowExit;
    private readonly UpdateChecker _updater = new();
    private readonly RichPresenceService _rp = new();
    private bool _rpEnabled;

    private static Version CurrentVersion
    {
        get { var v = typeof(AppShell).Assembly.GetName().Version; return v is null ? new Version(1, 0, 0) : new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build); }
    }

    // ----- auto-rejoin / keep-alive -------------------------------------------
    // Best-effort: protocol launches don't return a process handle, so we bind each
    // launched account to a RobloxPlayerBeta PID by snapshotting the process list
    // before/after launch. A timer watches the bound PIDs and relaunches any that exit.
    private bool _autoRejoin;
    private readonly List<KeepSession> _sessions = new();
    private readonly object _sessionLock = new();
    private System.Windows.Forms.Timer? _keepAliveTimer;
    private bool _keepAliveTicking;
    private bool _keepTiled; // when on, re-tile whenever a client window (re)appears or closes

    private sealed class KeepSession
    {
        public long UserId;
        public long PlaceId;
        public string? JobId, AccessCode, LinkCode;
        public int Pid;       // 0 = not yet captured / just died
        public bool Busy;     // a relaunch+capture is in flight
        public int Failures;  // consecutive relaunch failures (give up after a few)
        public string Alias = "";                 // for the running panel + window title
        public IntPtr Hwnd;                       // the client's main window (label / tile)
        public DateTime StartTime = DateTime.Now; // process start time (for uptime)
    }

    private async Task InitAsync()
    {
        TryStyleWindow();
        try
        {
            string udf = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prism", "webview-ui");
            Directory.CreateDirectory(udf);
            _web.CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = udf };
            await _web.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Prism's interface needs the Microsoft Edge WebView2 Runtime, which couldn't start.\n\n" +
                "Install it (free) from https://developer.microsoft.com/microsoft-edge/webview2/ and reopen Prism.\n\n" +
                "Details: " + ex.Message,
                "WebView2 required", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        var s = _web.CoreWebView2.Settings;
        s.AreDevToolsEnabled = false;
        s.AreDefaultContextMenusEnabled = false;
        s.IsStatusBarEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        _web.CoreWebView2.WebMessageReceived += OnWebMessage;
        if (Demo) _web.CoreWebView2.NavigationCompleted += OnDemoNavCompleted;

        // Reserve the singleton kernel objects ASAP so extra clients can launch.
        _lockMessage = _launcher.AcquireSingleInstanceLock();
        SetupTray();
        StartKeepAlive();

        _web.CoreWebView2.NavigateToString(ReadAppHtml());
    }

    // ----- message bridge -----------------------------------------------------

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try { json = e.TryGetWebMessageAsString(); } catch { return; }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch { return; }
        using (doc) await DispatchAsync(doc.RootElement);
    }

    private async Task DispatchAsync(JsonElement msg)
    {
        int id = msg.TryGetProperty("id", out var ide) && ide.TryGetInt32(out var iv) ? iv : 0;
        string action = msg.TryGetProperty("action", out var ae) ? ae.GetString() ?? "" : "";
        JsonElement p = msg.TryGetProperty("payload", out var pe) ? pe : default;

        try
        {
            switch (action)
            {
                case "ready":
                    if (Demo) { Reply(id, true); break; } // demo tour supplies its own data
                    Post(new { @event = "settings", settings = _settings });
                    Post(new { @event = "launchers", items = RobloxLauncher.Detect().Select(l => new { kind = l.Id, name = l.Name, installed = l.Installed }) });
                    SendStatus();
                    if (!string.IsNullOrEmpty(_lockMessage)) PushLog(_lockMessage);
                    if (_accounts.LoadWarning is { } w) PushLog(w);
                    PushLog(_accounts.Count == 0
                        ? "Welcome to Prism! Add an account to get started."
                        : $"Loaded {_accounts.Count} account(s). Tip: tick several, pick a server, then Launch to trade together.");
                    SendAccounts();
                    if (_rpEnabled) { _rp.Start(); ApplyPresence("accounts"); }
                    Post(new { @event = "appinfo", version = CurrentVersion.ToString() });
                    PushBackground();
                    _ = RefreshAllInfoAsync();
                    StartAutoRefresh();
                    _ = CheckUpdatesAsync(auto: true);
                    Reply(id, true);
                    break;
                case "loginRoblox": DoLogin(); Reply(id, true); break;
                case "addCookie": await DoAddCookieAsync(p); Reply(id, true); break;
                case "addCookiesBulk": await DoBulkImportAsync(p); Reply(id, true); break;
                case "remove": DoRemove(p); Reply(id, true); break;
                case "rename": DoRename(p); Reply(id, true); break;
                case "refresh": await DoRefreshAsync(p); Reply(id, true); break;
                case "findServers": Reply(id, true, new { servers = await DoFindServersAsync(p) }); break;
                case "launch": await DoLaunchAsync(p); Reply(id, true); break;
                case "window": DoWindow(p); Reply(id, true); break;
                case "presence": ApplyPresence(Str(p, "page")); Reply(id, true); break;
                case "setSetting":
                    {
                        string k = Str(p, "key"), v = Str(p, "value");
                        _settings[k] = v; SaveSettings();
                        if (k == "closeToTray") _closeToTray = v == "1";
                        else if (k == "minimizeToTray") _minimizeToTray = v == "1";
                        else if (k == "startWithWindows") ApplyStartup(v == "1");
                        else if (k == "richPresence")
                        {
                            _rpEnabled = v == "1";
                            _rp.SetEnabled(_rpEnabled);
                            if (_rpEnabled) ApplyPresence("accounts");
                        }
                        else if (k == "launcher")
                        {
                            _launcher.Preferred = RobloxLauncher.ParseKind(v);
                            PushLog(_launcher.Preferred == LauncherKind.Auto
                                ? "Launcher set to system default (whatever owns the roblox-player link)."
                                : $"Launcher set to {_launcher.ActiveLauncherLabel()} — clients will open through it.");
                        }
                        else if (k == "autoRejoin")
                        {
                            _autoRejoin = v == "1";
                            PushLog(_autoRejoin
                                ? "Auto-rejoin on — clients you launch will be reopened if they close."
                                : "Auto-rejoin off — running clients stay tracked, but won't be reopened.");
                        }
                        Reply(id, true); break;
                    }
                case "getStats": Reply(id, true, await BuildStatsAsync()); break;
                case "discover": Reply(id, true, await BuildDiscoverAsync()); break;
                case "searchGames": Reply(id, true, new { games = await GamesWithIconsAsync(await _api.SearchGamesAsync(Str(p, "query"))) }); break;
                case "demoDone": _ = FinishDemoAsync(); Reply(id, true); break;
                case "checkUpdate":
                    {
                        UpdateInfo? info = null;
                        try { info = await _updater.CheckAsync(CurrentVersion); } catch { }
                        if (info is not null) Post(new { @event = "update", version = info.Version, url = info.Url, notes = info.Notes });
                        Reply(id, true, new { update = info is not null, version = info?.Version, current = CurrentVersion.ToString() });
                        break;
                    }
                case "relaunchLast":
                    {
                        string saved = _settings.GetValueOrDefault("lastSession") ?? "";
                        if (string.IsNullOrWhiteSpace(saved)) { Toast("No previous session to relaunch", "err"); Reply(id, true); break; }
                        try { using var sdoc = JsonDocument.Parse(saved); await DoLaunchAsync(sdoc.RootElement); }
                        catch (Exception ex) { PushLog("Relaunch failed: " + ex.Message); }
                        Reply(id, true); break;
                    }
                case "installUpdate": _ = DoSelfUpdateAsync(); Reply(id, true); break;
                case "openExternal":
                    {
                        string url = Str(p, "url");
                        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                            (url.Contains("github.com", StringComparison.OrdinalIgnoreCase) || url.Contains("roblox.com", StringComparison.OrdinalIgnoreCase)))
                            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
                        Reply(id, true); break;
                    }
                case "openLogs":
                    {
                        try { Directory.CreateDirectory(Log.Dir); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Log.Dir}\"") { UseShellExecute = true }); } catch { }
                        Reply(id, true); break;
                    }
                case "pickBackground": PickBackground(); Reply(id, true); break;
                case "clearBackground": ClearBackground(); Reply(id, true); break;
                case "setBackgroundUrl": await SetBackgroundFromUrlAsync(Str(p, "url")); Reply(id, true); break;
                case "closeAllClients":
                    {
                        List<int> pids;
                        lock (_sessionLock) { pids = _sessions.Where(s => s.Pid != 0).Select(s => s.Pid).ToList(); _sessions.Clear(); }
                        int n = 0; foreach (var pid in pids) { try { using var pr = Process.GetProcessById(pid); pr.Kill(); n++; } catch { } }
                        PushLog($"Closed {n} client(s)."); PushClients();
                        Reply(id, true); break;
                    }
                case "closeClient":
                    {
                        long uid = Long(p, "userId"); int pid = 0;
                        lock (_sessionLock) { var s = _sessions.FirstOrDefault(x => x.UserId == uid); if (s is not null) { pid = s.Pid; _sessions.Remove(s); } }
                        if (pid != 0) { try { using var pr = Process.GetProcessById(pid); pr.Kill(); } catch { } }
                        PushClients(); if (_keepTiled) TileClients(); Reply(id, true); break;
                    }
                case "tileClients":
                    {
                        _keepTiled = !_keepTiled;
                        if (_keepTiled) TileClients();
                        else PushLog("Auto-tile off — windows left where they are.");
                        PushClients();
                        Reply(id, true); break;
                    }
                default: Reply(id, false, null, "Unknown action: " + action); break;
            }
        }
        catch (Exception ex)
        {
            Log.Exception($"action '{action}'", ex);
            Reply(id, false, null, ex.Message);
        }
    }

    // ----- actions ------------------------------------------------------------

    private void DoLogin()
    {
        // Guard against a second login window opening (e.g. a fast double-click): two
        // LoginForms share one cookie profile and would clobber each other mid-login.
        if (_loginOpen) return;
        _loginOpen = true;

        // IMPORTANT: defer to a fresh message-loop turn. This handler runs inside the main
        // WebView2's WebMessageReceived callback; opening a modal dialog (nested message loop)
        // AND creating a second WebView2 from there re-enters the runtime and deadlocks the
        // login window on "Loading Roblox…". BeginInvoke runs after this callback unwinds.
        BeginInvoke(new Action(async () =>
        {
            try
            {
                using var dlg = new LoginForm(_api);
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.CapturedCookie is null) return;

                SetBusy(true);
                try
                {
                    var acct = await _accounts.AddAccountAsync(dlg.User?.Name ?? "", dlg.CapturedCookie, null);
                    PushLog($"Added {acct.DisplayName} via login.");
                    Toast($"Added {acct.DisplayName}", "ok");
                    SendAccounts();
                }
                finally { SetBusy(false); }
            }
            catch (Exception ex) { Log.Exception("DoLogin", ex); Toast("Login failed — see logs", "err"); }
            finally { _loginOpen = false; }
        }));
    }

    private async Task DoAddCookieAsync(JsonElement p)
    {
        SetBusy(true);
        try
        {
            PushLog("Validating cookie with Roblox…");
            var acct = await _accounts.AddAccountAsync(Str(p, "alias"), Str(p, "cookie"), Str(p, "note"));
            PushLog($"Added {acct.DisplayName}.");
            SendAccounts();
        }
        finally { SetBusy(false); }
    }

    private async Task DoBulkImportAsync(JsonElement p)
    {
        var entries = new List<(string alias, string cookie)>();
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("entries", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
            {
                string alias = e.TryGetProperty("alias", out var a) ? a.GetString() ?? "" : "";
                string cookie = e.TryGetProperty("cookie", out var c) ? c.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(cookie)) entries.Add((alias, cookie));
            }
        if (entries.Count == 0) { Toast("Nothing to import", "err"); return; }

        SetBusy(true);
        int added = 0, failed = 0;
        try
        {
            PushLog($"Importing {entries.Count} account(s)…");
            foreach (var (alias, cookie) in entries)
            {
                try
                {
                    var acct = await _accounts.AddAccountAsync(alias, cookie, null);
                    PushLog($"  ✓ {acct.DisplayName}");
                    added++;
                    SendAccounts();
                }
                catch (Exception ex)
                {
                    PushLog($"  ✗ {(string.IsNullOrWhiteSpace(alias) ? "(cookie)" : alias)}: {ex.Message}");
                    failed++;
                }
                await Task.Delay(250); // gentle on Roblox rate limits
            }
            PushLog($"Import finished: {added} added, {failed} failed.");
            Toast($"Imported {added}" + (failed > 0 ? $", {failed} failed" : ""), failed > 0 ? "" : "ok");
            SendAccounts();
        }
        finally { SetBusy(false); }
    }

    private void DoRemove(JsonElement p)
    {
        var targets = TargetsFrom(p);
        if (targets.Count == 0) { Toast("Select the account(s) to remove", "err"); return; }
        foreach (var a in targets) _accounts.Remove(a);
        var removed = targets.Where(a => a.UserId is long).Select(a => a.UserId!.Value).ToHashSet();
        lock (_sessionLock) _sessions.RemoveAll(s => removed.Contains(s.UserId));
        PushClients();
        PushLog($"Removed {targets.Count} account(s).");
        SendAccounts();
    }

    private void DoRename(JsonElement p)
    {
        long userId = Long(p, "userId");
        string alias = Str(p, "alias");
        var acct = _accounts.Snapshot().FirstOrDefault(a => a.UserId == userId);
        if (acct is null || string.IsNullOrWhiteSpace(alias)) return;
        _accounts.Rename(acct, alias);
        PushLog($"Renamed to \"{alias.Trim()}\".");
        SendAccounts();
    }

    private async Task DoRefreshAsync(JsonElement p)
    {
        var targets = TargetsFrom(p);
        if (targets.Count == 0) targets = _accounts.Snapshot().ToList();
        if (targets.Count == 0) { Toast("No accounts to refresh", "err"); return; }

        SetBusy(true);
        try
        {
            foreach (var a in targets)
            {
                try
                {
                    bool ok = await _accounts.RefreshAsync(a);
                    PushLog(ok ? $"Refreshed {a.DisplayName}." : $"{a.Alias}: cookie expired — re-add this account.");
                }
                catch (Exception ex) { PushLog($"{a.Alias}: {ex.Message}"); }
            }
            _avatarUrls.Clear();
            SendAccounts();
        }
        finally { SetBusy(false); }
    }

    private async Task<object[]> DoFindServersAsync(JsonElement p)
    {
        long placeId = Long(p, "placeId");
        if (placeId <= 0) { Toast("Enter a numeric Place ID first", "err"); return Array.Empty<object>(); }
        PushLog($"Finding public servers for place {placeId}…");
        var servers = await _api.GetPublicServersAsync(placeId);
        PushLog(servers.Count > 0
            ? $"Found {servers.Count} server(s). Pick one (most free slots first) to send alts together."
            : "No public servers found (the game may have join restrictions).");
        return servers.Select(s => (object)new { jobId = s.JobId, playing = s.Playing, max = s.MaxPlayers, free = s.FreeSlots }).ToArray();
    }

    private async Task DoLaunchAsync(JsonElement p)
    {
        var targets = TargetsFrom(p);
        if (targets.Count == 0) { Toast("Tick at least one account to launch", "err"); return; }

        long placeField = Long(p, "placeId");
        string? jobId = Str(p, "jobId"); if (string.IsNullOrWhiteSpace(jobId)) jobId = null;
        var (linkPlaceId, linkCode, accessCode, shareCode, unsupported) = ParsePrivate(Str(p, "private"));

        if (unsupported)
        {
            PushLog("That looks like a link Prism can't resolve. Paste the game's private-server share link " +
                    "(the new roblox.com/share?code=… link works too) or the access-code GUID.");
            Toast("Unsupported private-server link", "err");
            return;
        }

        SetBusy(true);
        try
        {
            // NEW share-link format: resolve the opaque code → place + private-server join info,
            // using the first selected account's session.
            if (shareCode is not null)
            {
                PushLog("Resolving Roblox share link…");
                ShareLinkResult? sl = null;
                try { sl = await _accounts.ResolveShareLinkAsync(targets[0], shareCode); }
                catch (Exception ex) { PushLog("Couldn't resolve the share link: " + ex.Message); }
                if (sl is null || (sl.LinkCode is null && sl.AccessCode is null && sl.JobId is null))
                {
                    PushLog($"Couldn't turn that share link into a server. Make sure it's valid and that " +
                            $"{targets[0].Alias} can access it. Not launching.");
                    Toast("Couldn't resolve the share link", "err");
                    return;
                }
                if (sl.PlaceId > 0) linkPlaceId = sl.PlaceId;
                linkCode = sl.LinkCode;
                accessCode = sl.AccessCode;
                if (linkCode is null && accessCode is null && sl.JobId is not null) jobId = sl.JobId;
                PushLog("Share link resolved.");
            }

            bool isPrivate = linkCode is not null || accessCode is not null;
            if (isPrivate) jobId = null;

            long placeId;
            if ((isPrivate || jobId is not null) && linkPlaceId is { } lp) placeId = lp;
            else if (placeField > 0) placeId = placeField;
            else { Toast("Enter a numeric Place ID first", "err"); return; }

            if (isPrivate && accessCode is null && linkCode is not null)
            {
                if (shareCode is not null)
                {
                    // New share links return a Roblox-authoritative private-server link code
                    // (status "Valid"); launch straight with it. The old accessCode HTML-scrape
                    // no longer works on the modern game page.
                    PushLog("Joining the private server via its share-link code…");
                }
                else
                {
                    PushLog("Resolving private-server access code…");
                    try { accessCode = await _accounts.ResolvePrivateAccessCodeAsync(targets[0], placeId, linkCode); }
                    catch (Exception ex) { PushLog("Couldn't resolve access code: " + ex.Message); }
                    if (accessCode is null)
                    {
                        PushLog($"Couldn't get the private-server access code from that link. Check the link is correct and " +
                                $"that {targets[0].Alias} can access the server. Not launching (to avoid joining a public server).");
                        Toast("Couldn't resolve the private server", "err");
                        return;
                    }
                }
            }

            if (!_launcher.MultiInstanceActive && targets.Count > 1)
                PushLog("Warning: multi-instance is OFF — only one client may stay open. Close all Roblox windows and restart Prism.");

            if (_launcher.Preferred != LauncherKind.Auto)
            {
                if (_launcher.PreferredMissing)
                    Toast($"{_launcher.Preferred} isn't installed — opening with your default Roblox launcher instead", "err");
                PushLog($"Launching through {_launcher.ActiveLauncherLabel()}.");
            }

            // Snapshot existing Roblox clients so we can tell which PIDs each launch spawns
            // (powers the running panel, window labeling, tiling, and auto-rejoin).
            var preBatch = RobloxPidsSet();

            // Remember this batch so "Relaunch last session" can reopen it.
            try
            {
                _settings["lastSession"] = JsonSerializer.Serialize(new
                {
                    userIds = targets.Where(a => a.UserId is long).Select(a => a.UserId!.Value).ToArray(),
                    placeId,
                    jobId = Str(p, "jobId"),
                    @private = Str(p, "private"),
                });
                SaveSettings();
            }
            catch { }

            for (int i = 0; i < targets.Count; i++)
            {
                var a = targets[i];
                try
                {
                    await _accounts.LaunchAsync(a, placeId, jobId, accessCode, linkCode);
                    string where = isPrivate ? "private server" : jobId is not null ? $"server {Short(jobId)}" : "any server";
                    PushLog($"Launched {a.DisplayName} → place {placeId} ({where}).");

                    _stats.TotalLaunches++;
                    string pk = placeId.ToString();
                    _stats.ByPlace[pk] = _stats.ByPlace.GetValueOrDefault(pk) + 1;
                    if (a.UserId is long uid) _stats.ByAccount[uid.ToString()] = _stats.ByAccount.GetValueOrDefault(uid.ToString()) + 1;
                    _stats.LastPlaceId = placeId;
                    _stats.History.Add(new LaunchEntry { PlaceId = placeId, UserId = a.UserId ?? 0, Time = DateTime.UtcNow.ToString("o") });
                    if (_stats.History.Count > 500) _stats.History.RemoveRange(0, _stats.History.Count - 500);

                    if (a.UserId is long ruid)
                        RegisterSession(ruid, a.DisplayName, placeId, jobId, accessCode, linkCode, preBatch);

                    SaveStats(); // persist each successful launch so a crash mid-batch can't lose the lot
                }
                catch (Exception ex) { PushLog($"{a.Alias}: {ex.Message}"); }

                // Bootstrappers serialize their own bootstrap + update pass, so give them more room.
                if (i < targets.Count - 1) await Task.Delay(_launcher.UsesBootstrapper ? 3500 : 1800);
            }
            SaveStats();
            if (_rpEnabled) _rp.Update(null, $"Launched {targets.Count} client{(targets.Count == 1 ? "" : "s")}");
        }
        finally { SetBusy(false); }
    }

    // ----- auto-rejoin / keep-alive -------------------------------------------

    /// <summary>
    /// In-app update: download the latest Prism.exe next to the running one, then hand off to a
    /// tiny batch that waits for us to exit, swaps the file, and relaunches. Falls back to opening
    /// the download in the browser if we can't write here (e.g. installed under Program Files).
    /// </summary>
    private async Task DoSelfUpdateAsync()
    {
        const string url = "https://github.com/Cosmic4796/Prism/releases/latest/download/Prism.exe";
        string exe = Environment.ProcessPath ?? "";
        string newPath = exe + ".new";
        try
        {
            if (string.IsNullOrEmpty(exe)) { OpenInBrowser(url); return; }
            try { File.WriteAllText(newPath, ""); }                 // can we write next to the exe?
            catch { Toast("Can't auto-update from this folder — opening the download…", ""); OpenInBrowser(url); return; }

            Toast("Downloading update…", "");
            SetBusy(true);
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Prism-Updater");
                await File.WriteAllBytesAsync(newPath, await http.GetByteArrayAsync(url));
            }

            int pid = Environment.ProcessId;
            string bat = Path.Combine(Path.GetTempPath(), "prism-update.cmd");
            File.WriteAllText(bat,
                "@echo off\r\n:wait\r\n" +
                $"tasklist /fi \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
                $"move /y \"{newPath}\" \"{exe}\" >nul\r\n" +
                $"start \"\" \"{exe}\"\r\n" +
                "del \"%~f0\"\r\n");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"") { UseShellExecute = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            Toast("Updating — Prism will restart…", "ok");
            await Task.Delay(700);
            Application.Exit();
        }
        catch (Exception ex)
        {
            SetBusy(false);
            try { File.Delete(newPath); } catch { }
            Log.Exception("selfUpdate", ex);
            OpenInBrowser(url);
        }
    }

    private void OpenInBrowser(string url)
    {
        try { if (!string.IsNullOrEmpty(url)) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        Toast("Opened the download in your browser", "");
    }

    private void StartKeepAlive()
    {
        _keepAliveTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _keepAliveTimer.Tick += async (_, _) => await KeepAliveTickAsync();
        _keepAliveTimer.Start();
    }

    /// <summary>Tracks (or refreshes) a launched account and binds its new PID. Runs for
    /// every launch (powers the running panel / labeling / tiling), not just auto-rejoin.</summary>
    private void RegisterSession(long userId, string alias, long placeId, string? jobId, string? accessCode, string? linkCode, HashSet<int> preExisting)
    {
        KeepSession s;
        lock (_sessionLock)
        {
            s = _sessions.FirstOrDefault(x => x.UserId == userId) ?? AddSession(userId);
            s.Alias = alias; s.PlaceId = placeId; s.JobId = jobId; s.AccessCode = accessCode; s.LinkCode = linkCode;
            s.Pid = 0; s.Hwnd = IntPtr.Zero; s.Failures = 0; s.Busy = false;
        }
        _ = CapturePidAsync(s, preExisting);

        KeepSession AddSession(long uid) { var n = new KeepSession { UserId = uid }; _sessions.Add(n); return n; }
    }

    /// <summary>
    /// Polls for the RobloxPlayerBeta process this launch spawned and binds it to the
    /// session. A candidate is any client that wasn't running before the launch and
    /// isn't already claimed by another session; we prefer the most-recently-started.
    /// </summary>
    private async Task CapturePidAsync(KeepSession s, HashSet<int> preExisting)
    {
        // A first-run bootstrapper may download/extract a Roblox update before RobloxPlayerBeta
        // appears, so give those launches a much longer window before giving up on the PID.
        int maxPolls = _launcher.UsesBootstrapper ? 75 : 25;
        for (int i = 0; i < maxPolls && !IsDisposed; i++)
        {
            await Task.Delay(1000);
            int picked = 0;
            lock (_sessionLock)
            {
                if (!_sessions.Contains(s)) return; // session was cleared/removed
                var claimed = _sessions.Where(x => x != s && x.Pid != 0).Select(x => x.Pid).ToHashSet();
                int pick = 0; DateTime newest = DateTime.MinValue;
                foreach (var (pid, started) in RobloxProcesses())
                {
                    if (preExisting.Contains(pid) || claimed.Contains(pid)) continue;
                    if (started >= newest) { newest = started; pick = pid; }
                }
                if (pick != 0) { s.Pid = pick; s.StartTime = newest == DateTime.MinValue ? DateTime.Now : newest; picked = pick; }
            }
            if (picked != 0) { PushClients(); _ = LabelWindowAsync(s, picked); return; }
        }
    }

    /// <summary>Every few seconds: prune closed clients, keep their window titles applied,
    /// push the running list to the UI, and — if auto-rejoin is on — reopen any that exited.</summary>
    private async Task KeepAliveTickAsync()
    {
        if (_keepAliveTicking || IsDisposed) return;

        var live = RobloxProcesses().Select(p => p.pid).ToHashSet();
        List<KeepSession> dead;
        lock (_sessionLock)
        {
            dead = _sessions.Where(s => !s.Busy && s.Pid != 0 && !live.Contains(s.Pid)).ToList();
            if (_autoRejoin) { foreach (var s in dead) s.Busy = true; }          // claim for relaunch
            else { foreach (var s in dead) _sessions.Remove(s); dead.Clear(); }   // just untrack closed ones
        }
        RelabelClients(live);   // Roblox overwrites titles once the game loads; re-apply
        PushClients();

        // If we couldn't grab the multi-instance lock at startup (Roblox was open), keep trying —
        // once every Roblox window is closed we can claim it, and the status flips to ON live.
        if (!_launcher.MultiInstanceActive && live.Count == 0)
        {
            _launcher.AcquireSingleInstanceLock();
            if (_launcher.MultiInstanceActive) { SendStatus(); PushLog("Multi-instance enabled (Roblox fully closed)."); }
        }

        if (dead.Count == 0) return;

        _keepAliveTicking = true;
        try
        {
            foreach (var s in dead)
            {
                var acct = _accounts.Snapshot().FirstOrDefault(a => a.UserId == s.UserId);
                if (acct is null) { lock (_sessionLock) _sessions.Remove(s); continue; }

                if (s.Failures >= 3)
                {
                    PushLog($"Auto-rejoin: giving up on {acct.DisplayName} after repeated failures (check its cookie).");
                    lock (_sessionLock) _sessions.Remove(s);
                    continue;
                }

                s.Pid = 0; s.Hwnd = IntPtr.Zero;
                PushLog($"Auto-rejoin: {acct.DisplayName} closed — relaunching…");
                var pre = RobloxProcesses().Select(p => p.pid).ToHashSet();
                try
                {
                    await _accounts.LaunchAsync(acct, s.PlaceId, s.JobId, s.AccessCode, s.LinkCode);
                    s.Failures = 0;
                    await CapturePidAsync(s, pre);
                    if (s.Pid == 0) PushLog($"Auto-rejoin: relaunched {acct.DisplayName} (couldn't confirm its window).");
                }
                catch (Exception ex)
                {
                    s.Failures++;
                    PushLog($"Auto-rejoin {acct.Alias}: {ex.Message}");
                }
                finally { s.Busy = false; }

                if (dead.Count > 1) await Task.Delay(_launcher.UsesBootstrapper ? 3500 : 1500); // stagger multiple relaunches
            }
        }
        finally { _keepAliveTicking = false; PushClients(); }
    }

    /// <summary>Pushes the currently-tracked running clients (alias + start time) to the UI.</summary>
    private void PushClients()
    {
        object[] arr;
        lock (_sessionLock)
            arr = _sessions.Where(s => s.Pid != 0)
                .Select(s => (object)new
                {
                    userId = s.UserId,
                    alias = s.Alias,
                    startedMs = new DateTimeOffset(s.StartTime).ToUnixTimeMilliseconds(),
                })
                .ToArray();
        Post(new { @event = "clients", clients = arr, tiled = _keepTiled });
    }

    /// <summary>Once a client's main window appears, title it "alias — Prism" so the taskbar
    /// tells the alts apart.</summary>
    private async Task LabelWindowAsync(KeepSession s, int pid)
    {
        for (int i = 0; i < 20 && !IsDisposed; i++)
        {
            try
            {
                using var pr = Process.GetProcessById(pid);
                pr.Refresh();
                IntPtr h = pr.MainWindowHandle;
                if (h != IntPtr.Zero)
                {
                    s.Hwnd = h;
                    try { SetWindowText(h, $"{s.Alias} — Prism"); } catch { }
                    if (_keepTiled) TileClients(); // a (re)launched window appeared — re-snap the grid
                    return;
                }
            }
            catch { return; } // process gone
            await Task.Delay(1000);
        }
    }

    /// <summary>Re-applies window titles for live clients (Roblox resets them on load).</summary>
    private void RelabelClients(HashSet<int> live)
    {
        List<(IntPtr h, string alias)> items;
        lock (_sessionLock)
            items = _sessions.Where(s => s.Pid != 0 && s.Hwnd != IntPtr.Zero && live.Contains(s.Pid))
                .Select(s => (s.Hwnd, s.Alias)).ToList();
        foreach (var (h, alias) in items)
            try { SetWindowText(h, $"{alias} — Prism"); } catch { }
    }

    /// <summary>Arranges the running client windows into a grid on the primary monitor.</summary>
    private void TileClients()
    {
        List<IntPtr> hwnds;
        lock (_sessionLock) hwnds = _sessions.Where(s => s.Pid != 0 && s.Hwnd != IntPtr.Zero).Select(s => s.Hwnd).ToList();
        if (hwnds.Count == 0) { Toast("No running clients to tile", "err"); return; }
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        int n = hwnds.Count;
        int cols = (int)Math.Ceiling(Math.Sqrt(n));
        int rows = (int)Math.Ceiling((double)n / cols);
        int cw = wa.Width / cols, ch = wa.Height / rows;
        for (int i = 0; i < n; i++)
        {
            int r = i / cols, c = i % cols;
            try
            {
                ShowWindow(hwnds[i], SW_RESTORE);
                SetWindowPos(hwnds[i], IntPtr.Zero, wa.Left + c * cw, wa.Top + r * ch, cw, ch, SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch { }
        }
        PushLog($"Tiled {n} client(s).");
    }

    /// <summary>Currently-running Roblox clients with their start times (best-effort).</summary>
    private static List<(int pid, DateTime started)> RobloxProcesses()
    {
        var list = new List<(int, DateTime)>();
        Process[] procs;
        try { procs = Process.GetProcessesByName("RobloxPlayerBeta"); } catch { return list; }
        foreach (var pr in procs)
        {
            try
            {
                DateTime st; try { st = pr.StartTime; } catch { st = DateTime.MinValue; }
                list.Add((pr.Id, st));
            }
            catch { /* process vanished between enumerate and read */ }
            finally { try { pr.Dispose(); } catch { } }
        }
        return list;
    }

    private static HashSet<int> RobloxPidsSet() => RobloxProcesses().Select(p => p.pid).ToHashSet();

    // ----- demo capture -------------------------------------------------------

    private void OnDemoNavCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _web.CoreWebView2.NavigationCompleted -= OnDemoNavCompleted;
        _ = RunDemoAsync();
    }

    private async Task RunDemoAsync()
    {
        try
        {
            Show(); WindowState = FormWindowState.Normal; TopMost = true; Activate();
            try { Directory.CreateDirectory(_demoFrameDir!); } catch { }

            // DevTools screencast: Chromium pushes a real PNG frame on every repaint
            // (up to display rate during motion). Genuine frames — no interpolation.
            var rcv = _web.CoreWebView2.GetDevToolsProtocolEventReceiver("Page.screencastFrame");
            rcv.DevToolsProtocolEventReceived += OnScreencastFrame;
            await _web.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
            await _web.CoreWebView2.CallDevToolsProtocolMethodAsync(
                "Page.startScreencast", "{\"format\":\"png\",\"everyNthFrame\":1}");
            await Task.Delay(60);

            string js;
            try { js = File.ReadAllText(_demoScriptPath!); }
            catch (Exception ex) { PushLog("demo: couldn't read tour script — " + ex.Message); return; }
            await _web.CoreWebView2.ExecuteScriptAsync(js);
        }
        catch (Exception ex) { PushLog("demo error: " + ex.Message); }
    }

    private async void OnScreencastFrame(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        string? sid = null;
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var root = doc.RootElement;
            string data = root.GetProperty("data").GetString() ?? "";
            double ts = root.TryGetProperty("metadata", out var md) && md.TryGetProperty("timestamp", out var t)
                && t.ValueKind == JsonValueKind.Number ? t.GetDouble() : 0;
            sid = root.TryGetProperty("sessionId", out var s) ? s.GetRawText() : "0";

            int idx = _scFrame++;
            try { File.WriteAllBytes(Path.Combine(_demoFrameDir!, $"f{idx:00000}.png"), Convert.FromBase64String(data)); } catch { }
            _scTimes.Add($"{idx} {ts:0.######}");
        }
        catch { }
        // Ack so Chromium keeps streaming the next frame.
        try { await _web.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.screencastFrameAck", $"{{\"sessionId\":{sid ?? "0"}}}"); } catch { }
    }

    private async Task FinishDemoAsync()
    {
        if (_demoFinishing) return;
        _demoFinishing = true;
        try { await _web.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.stopScreencast", "{}"); } catch { }
        try { File.WriteAllLines(Path.Combine(_demoFrameDir!, "times.txt"), _scTimes); } catch { }
        try { File.WriteAllText(Path.Combine(_demoFrameDir!, "done.flag"), _scFrame.ToString()); } catch { }
        if (!IsDisposed) BeginInvoke(new Action(() => { _allowExit = true; TopMost = false; Close(); }));
    }

    // ----- helpers ------------------------------------------------------------

    private List<Account> TargetsFrom(JsonElement p)
    {
        var ids = new HashSet<long>();
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("userIds", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var el in arr.EnumerateArray())
                if (el.TryGetInt64(out var v)) ids.Add(v);
        return _accounts.Snapshot().Where(a => a.UserId is long u && ids.Contains(u)).ToList();
    }

    private void SendAccounts()
    {
        var list = _accounts.Snapshot();
        Post(new
        {
            @event = "accounts",
            accounts = list.Select(a =>
            {
                _acctInfo.TryGetValue(a.UserId ?? -1, out var inf);
                string? avatar = a.UserId is long uid && _avatarUrls.TryGetValue(uid, out var url) ? url : null;
                return (object)new
                {
                    userId = a.UserId,
                    alias = a.Alias,
                    username = a.Username,
                    displayName = a.DisplayName,
                    note = a.Note,
                    avatar,
                    health = inf.health,
                    robux = inf.robux,
                    premium = inf.premium,
                };
            }).ToArray(),
        });

        foreach (var a in list)
            if (a.UserId is long uid && !_avatarUrls.ContainsKey(uid))
                _ = ResolveAvatarAsync(uid);
    }

    /// <summary>Validates every account (auto-refreshing cookies) and fetches Robux/Premium, staggered.</summary>
    private async Task RefreshAllInfoAsync()
    {
        if (_infoRunning) return;
        _infoRunning = true;
        try
        {
            foreach (var a in _accounts.Snapshot())
            {
                if (a.UserId is not long uid) continue;
                Post(new { @event = "acctinfo", userId = uid, health = "checking", robux = (long?)null, premium = (bool?)null });
                try
                {
                    var (ok, robux, premium) = await _accounts.GetAccountStatusAsync(a);
                    _acctInfo[uid] = (ok ? "ok" : "dead", robux, premium);
                    Post(new { @event = "acctinfo", userId = uid, health = ok ? "ok" : "dead", robux, premium });
                }
                catch
                {
                    _acctInfo[uid] = ("unknown", null, null);
                    Post(new { @event = "acctinfo", userId = uid, health = "unknown", robux = (long?)null, premium = (bool?)null });
                }
                await Task.Delay(900);
            }
        }
        finally { _infoRunning = false; }
    }

    private async void StartAutoRefresh()
    {
        if (_autoStarted) return;
        _autoStarted = true;
        while (!IsDisposed)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(12)); } catch { break; }
            if (IsDisposed) break;
            try { await RefreshAllInfoAsync(); } catch { }
        }
    }

    /// <summary>Checks GitHub for a newer release; posts an "update" event if found.
    /// Set the PRISM_FORCE_UPDATE env var to simulate one (for testing the update UI).</summary>
    private async Task CheckUpdatesAsync(bool auto)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PRISM_FORCE_UPDATE")))
        {
            Post(new { @event = "update", version = "9.9.9", url = "https://github.com/Cosmic4796/Prism/releases/latest",
                notes = "Test update — simulated release to verify the update banner + changelog UI." });
            return;
        }
        try
        {
            var info = await _updater.CheckAsync(CurrentVersion);
            if (info is not null)
                Post(new { @event = "update", version = info.Version, url = info.Url, notes = info.Notes });
        }
        catch { }
        _ = auto; // (reserved: could differ later for manual vs auto)
    }

    private async Task ResolveAvatarAsync(long userId)
    {
        string? url = await _api.GetHeadshotThumbnailUrlAsync(userId);
        if (string.IsNullOrEmpty(url)) return;
        _avatarUrls[userId] = url;
        Post(new { @event = "avatar", userId, url });
    }

    private void SendStatus() => Post(new
    {
        @event = "status",
        on = _launcher.MultiInstanceActive,
        text = _launcher.MultiInstanceActive ? "Multi-instance: ON" : "Multi-instance: OFF",
    });

    /// <summary>Refreshes Discord Rich Presence: top line = account count, bottom = current page.</summary>
    private void ApplyPresence(string? page)
    {
        if (!_rpEnabled) return;
        int n = _accounts.Count;
        string details = n > 0 ? $"Managing {n} account{(n == 1 ? "" : "s")}" : "Multi-accounting Roblox";
        string state = (page ?? "").ToLowerInvariant() switch
        {
            "discover" => "Browsing games",
            "analytics" => "Checking the stats",
            "customize" => "Customizing the look",
            "settings" => "In settings",
            _ => "In the launcher",
        };
        _rp.Update(details, state);
    }

    private void PushLog(string message) => Post(new { @event = "log", message });
    private void SetBusy(bool on) => Post(new { @event = "busy", on });
    private void Toast(string message, string kind) => Post(new { @event = "toast", message, kind });
    private void Reply(int id, bool ok, object? result = null, string? error = null) => Post(new { id, ok, result, error });

    private void Post(object payload)
    {
        string json = JsonSerializer.Serialize(payload);
        void Send() { try { _web.CoreWebView2?.PostWebMessageAsString(json); } catch { /* shutting down */ } }
        try
        {
            if (_web.IsDisposed) return;
            if (_web.InvokeRequired) _web.BeginInvoke(Send);
            else Send();
        }
        catch { /* form closing */ }
    }

    private static string Short(string jobId) => jobId.Length <= 8 ? jobId : jobId[..8] + "…";

    // ----- frameless window controls ------------------------------------------

    private void DoWindow(JsonElement p)
    {
        switch (Str(p, "op"))
        {
            case "minimize":
                WindowState = FormWindowState.Minimized;
                break;
            case "maximize":
                WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                break;
            case "close":
                Close();
                break;
            case "drag":
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                break;
            case "resize":
                if (WindowState != FormWindowState.Normal) break;
                int ht = Str(p, "dir") switch
                {
                    "w" => 10, "e" => 11, "n" => 12, "nw" => 13, "ne" => 14, "s" => 15, "sw" => 16, "se" => 17, _ => 0,
                };
                if (ht != 0) { ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)ht, IntPtr.Zero); }
                break;
        }
    }

    private void PushWinState() => Post(new { @event = "winstate", maximized = WindowState == FormWindowState.Maximized });

    private void TryStyleWindow()
    {
        // Ask DWM for square corners + no border highlight…
        try { int pref = 1; DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); } catch { }
        try { int none = unchecked((int)0xFFFFFFFE); DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref none, sizeof(int)); } catch { }
        // …and force it: clip the window to a rectangle so Win11 can't round the
        // corners (a rounded corner is transparent and shows the desktop behind it
        // as a stray "dot"). Must be re-applied whenever the window size changes.
        ClipSquare();
    }

    private void ClipSquare()
    {
        if (!IsHandleCreated) return;
        try { SetWindowRgn(Handle, CreateRectRgn(0, 0, Width, Height), true); } catch { }
    }

    private void SetupTray()
    {
        _tray = new NotifyIcon { Text = "Prism — Roblox Account Manager", Visible = true };
        try { _tray.Icon = Theme.AppIcon; } catch { }
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Prism", null, (_, _) => ShowFromTray());
        menu.Items.Add("Quit Prism", null, (_, _) => { _allowExit = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        Post(new { @event = "restored" }); // reset any minimizing/closing animation state
    }

    private static void ApplyStartup(bool on)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null) return;
            if (on) key.SetValue("Prism", $"\"{Application.ExecutablePath}\"");
            else key.DeleteValue("Prism", throwOnMissingValue: false);
        }
        catch { /* registry access denied — non-fatal */ }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Re-apply once the window is actually displayed — Win11 applies its default
        // corner rounding on show, which can override an earlier square-corners request
        // (a rounded corner is transparent and reveals whatever is behind it as a "dot").
        TryStyleWindow();
    }

    protected override void WndProc(ref Message m)
    {
        // A borderless form maximizes over the taskbar; clamp it to the work area.
        if (m.Msg == WM_GETMINMAXINFO)
        {
            var scr = Screen.FromHandle(Handle);
            var wa = scr.WorkingArea; var b = scr.Bounds;
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(m.LParam);
            mmi.ptMaxPosition = new POINT { x = wa.Left - b.Left, y = wa.Top - b.Top };
            mmi.ptMaxSize = new POINT { x = wa.Width, y = wa.Height };
            mmi.ptMinTrackSize = new POINT { x = MinimumSize.Width, y = MinimumSize.Height };
            Marshal.StructureToPtr(mmi, m.LParam, true);
            return;
        }
        base.WndProc(ref m);
    }

    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int SW_RESTORE = 9;
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(IntPtr hWnd, string text);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }

    private static string Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static long Long(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
            ? n : 0;

    /// <summary>Parses the private-server box: a new share link/code, an old share link, a bare link code, or an access-code GUID.</summary>
    private static (long? placeId, string? linkCode, string? accessCode, string? shareCode, bool unsupported) ParsePrivate(string raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return (null, null, null, null, false);

        // NEW share-link format: roblox.com/share?code=<32 hex>&type=Server (also /share-links?code=…,
        // the roblox://navigation/share_links?code=… deep link, or a bare 32-hex code). The 32-hex code
        // is resolved to a place + private server via the sharelinks API at launch time.
        var sc = Regex.Match(raw, @"(?:[?&]code=|share_links\?code=)([0-9a-fA-F]{32})", RegexOptions.IgnoreCase);
        if (sc.Success) return (null, null, null, sc.Groups[1].Value, false);
        if (Regex.IsMatch(raw, @"^[0-9a-fA-F]{32}$")) return (null, null, null, raw, false);

        long? placeId = null;
        var pm = Regex.Match(raw, @"/games/(\d+)");
        if (pm.Success && long.TryParse(pm.Groups[1].Value, out var p)) placeId = p;

        var lm = Regex.Match(raw, @"privateServerLinkCode=([\w\-]+)", RegexOptions.IgnoreCase);
        if (lm.Success) return (placeId, lm.Groups[1].Value, null, null, false);

        if (Guid.TryParse(raw, out _)) return (placeId, null, raw, null, false);
        if (Regex.IsMatch(raw, @"^[A-Za-z0-9_\-]{4,}$")) return (placeId, raw, null, null, false);
        return (null, null, null, null, true);
    }

    private async Task<object> BuildDiscoverAsync()
    {
        var sorts = await _api.GetDiscoverSortsAsync();
        var icons = await _api.GetGameIconsAsync(sorts.SelectMany(s => s.Games.Select(g => g.UniverseId)));
        var sections = sorts.Select(s => (object)new
        {
            title = s.Title,
            games = s.Games.Select(g => GameDto(g, icons)).ToArray(),
        }).ToArray();
        return new { sections };
    }

    private async Task<object[]> GamesWithIconsAsync(IReadOnlyList<RobloxGame> games)
    {
        var icons = await _api.GetGameIconsAsync(games.Select(g => g.UniverseId));
        return games.Select(g => GameDto(g, icons)).ToArray();
    }

    private static object GameDto(RobloxGame g, Dictionary<long, string> icons) => new
    {
        universeId = g.UniverseId,
        placeId = g.PlaceId,
        name = g.Name,
        playing = g.Playing,
        up = g.UpVotes,
        down = g.DownVotes,
        icon = icons.TryGetValue(g.UniverseId, out var u) ? u : null,
    };

    private async Task<object> BuildStatsAsync()
    {
        var list = _accounts.Snapshot();

        var topPlaces = _stats.ByPlace.OrderByDescending(kv => kv.Value).Take(6).ToList();
        var recentEntries = Enumerable.Reverse(_stats.History).Take(18).ToList();

        var placeIds = topPlaces.Select(kv => long.TryParse(kv.Key, out var v) ? v : 0)
            .Concat(recentEntries.Select(e => e.PlaceId))
            .Where(v => v > 0).Distinct().ToList();

        Dictionary<long, string> names = new();
        if (placeIds.Count > 0 && list.Count > 0)
        {
            try { names = await _accounts.GetPlaceNamesAsync(list[0], placeIds); } catch { }
        }

        string NameOf(string key) =>
            long.TryParse(key, out var pid) && names.TryGetValue(pid, out var n) && !string.IsNullOrEmpty(n)
                ? n : "Place " + key;
        string NameOfId(long pid) => names.TryGetValue(pid, out var n) && !string.IsNullOrEmpty(n) ? n : "Place " + pid;
        string AliasOf(long uid) => list.FirstOrDefault(a => a.UserId == uid)?.DisplayName ?? (uid > 0 ? "id " + uid : "—");

        var topGames = topPlaces.Select(kv => new
        {
            placeId = kv.Key, name = NameOf(kv.Key), count = kv.Value,
        }).ToArray();

        var topAccounts = _stats.ByAccount
            .OrderByDescending(kv => kv.Value)
            .Select(kv =>
            {
                var acc = list.FirstOrDefault(a => a.UserId?.ToString() == kv.Key);
                return acc is null ? null : (object)new { name = acc.DisplayName, count = kv.Value };
            })
            .Where(x => x is not null)
            .Take(6)
            .ToArray();

        var recent = recentEntries.Select(e => (object)new
        {
            game = NameOfId(e.PlaceId),
            account = AliasOf(e.UserId),
            ago = RelativeAgo(e.Time),
        }).ToArray();

        return new
        {
            accounts = list.Count,
            totalLaunches = _stats.TotalLaunches,
            multiInstance = _launcher.MultiInstanceActive,
            topGames,
            topAccounts,
            recent,
            // True when totals exist but the per-launch timeline doesn't — i.e. launches
            // from before the History feature shipped. Lets the UI show honest copy.
            historyGap = _stats.TotalLaunches > 0 && _stats.History.Count == 0,
        };
    }

    private static string RelativeAgo(string iso)
    {
        if (!DateTime.TryParse(iso, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t))
            return "";
        var d = DateTime.UtcNow - t;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }

    // ----- custom background image --------------------------------------------

    private void PickBackground()
    {
        using var dlg = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif", Title = "Choose a background image" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { using var src = System.Drawing.Image.FromFile(dlg.FileName); SaveBackgroundFromImage(src); Toast("Background updated", "ok"); }
        catch (Exception ex) { Toast("Couldn't load that image", "err"); Log.Exception("pickBackground", ex); }
    }

    private async Task SetBackgroundFromUrlAsync(string url)
    {
        url = (url ?? "").Trim();
        if (!(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        { Toast("Enter a valid image URL (http/https)", "err"); return; }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Prism");
            using var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) { Toast("Couldn't fetch that link", "err"); return; }
            byte[] data = await resp.Content.ReadAsByteArrayAsync();
            if (data.Length > 25_000_000) { Toast("That image is too large", "err"); return; }
            using var ms = new MemoryStream(data);
            using var src = System.Drawing.Image.FromStream(ms);
            SaveBackgroundFromImage(src);
            Toast("Background updated", "ok");
        }
        catch (Exception ex) { Toast("Couldn't load an image from that link", "err"); Log.Exception("setBackgroundUrl", ex); }
    }

    /// <summary>Downscale a loaded image to &lt;=1920x1080, save it as the background, persist + apply.</summary>
    private void SaveBackgroundFromImage(System.Drawing.Image src)
    {
        string outPath = Path.Combine(AppData.Dir, "background.jpg");
        double scale = Math.Min(1.0, Math.Min(1920.0 / src.Width, 1080.0 / src.Height));
        int w = Math.Max(1, (int)(src.Width * scale)), h = Math.Max(1, (int)(src.Height * scale));
        using (var bmp = new System.Drawing.Bitmap(w, h))
        {
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, w, h);
            }
            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Jpeg);
        }
        _settings["bgImage"] = outPath; SaveSettings();
        PushBackground();
    }

    private void PushBackground()
    {
        string path = _settings.GetValueOrDefault("bgImage") ?? "";
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                string uri = "data:image/jpeg;base64," + Convert.ToBase64String(File.ReadAllBytes(path));
                Post(new { @event = "background", uri });
                return;
            }
            catch { }
        }
        Post(new { @event = "background", uri = "" });
    }

    private void ClearBackground()
    {
        try { var p = _settings.GetValueOrDefault("bgImage"); if (!string.IsNullOrEmpty(p) && File.Exists(p)) File.Delete(p); } catch { }
        _settings.Remove("bgImage"); SaveSettings();
        PushBackground();
        Toast("Background cleared", "ok");
    }

    private void LoadStats()
    {
        try
        {
            if (File.Exists(_statsPath))
                _stats = JsonSerializer.Deserialize<Stats>(File.ReadAllText(_statsPath)) ?? new();
        }
        catch { _stats = new(); }
    }

    private void SaveStats()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statsPath)!);
            File.WriteAllText(_statsPath, JsonSerializer.Serialize(_stats));
        }
        catch { /* non-fatal */ }
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
                _settings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_settingsPath)) ?? new();
        }
        catch { _settings = new(); }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings));
        }
        catch { /* non-fatal: settings just won't persist */ }
    }

    private static string ReadAppHtml()
    {
        var asm = typeof(AppShell).Assembly;
        string? name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("app.html", StringComparison.OrdinalIgnoreCase));
        if (name is null) return "<h1 style='color:#fff;background:#16171A'>app.html resource missing</h1>";
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
