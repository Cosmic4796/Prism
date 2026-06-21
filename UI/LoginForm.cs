using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using RobloxMultiManager.Models;
using RobloxMultiManager.Services;

namespace RobloxMultiManager.UI;

/// <summary>
/// Logs into Roblox using an embedded browser (the REAL roblox.com login page, so
/// passwords/2FA/captcha all work normally). When a valid session appears, it reads
/// the HttpOnly .ROBLOSECURITY cookie straight from the browser's cookie store — no
/// copy-paste. Each login starts from a clean session so you can add alt after alt.
/// </summary>
public sealed class LoginForm : Form
{
    private readonly RobloxWebApi _api;
    private readonly WebView2 _web;
    private readonly Label _status;
    private readonly System.Windows.Forms.Timer _poll;
    private readonly CancellationTokenSource _cts = new();
    private bool _captured;
    private bool _checking;
    private bool _closing;

    /// <summary>The captured .ROBLOSECURITY cookie (set only on a successful login).</summary>
    public string? CapturedCookie { get; private set; }

    /// <summary>The authenticated user (set only on a successful login).</summary>
    public RobloxUser? User { get; private set; }

    public LoginForm(RobloxWebApi api)
    {
        _api = api;

        Text = "Log in with Roblox";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(480, 660);
        MinimumSize = new Size(420, 520);
        Theme.ApplyForm(this);

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 10, 0),
            ForeColor = Theme.SubText,
            BackColor = Theme.Surface,
            Text = "Loading Roblox…",
        };

        _web = new WebView2 { Dock = DockStyle.Fill };

        Controls.Add(_web);
        Controls.Add(_status);

        _poll = new System.Windows.Forms.Timer { Interval = 1500 };
        _poll.Tick += async (_, _) => await TryCaptureAsync();

        Load += async (_, _) => await InitAsync();
        FormClosing += (_, _) =>
        {
            // Make any in-flight capture/validate stop cooperatively instead of
            // resuming on a disposed form.
            _closing = true;
            _poll.Stop();
            _cts.Cancel();
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Dispose();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }

    private async Task InitAsync()
    {
        try
        {
            // A dedicated, reusable profile folder kept out of the repo/roaming data.
            string udf = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Prism", "login-webview");
            Directory.CreateDirectory(udf);

            _web.CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = udf };
            Log.Write($"login: EnsureCoreWebView2Async start (udf={udf})");
            await _web.EnsureCoreWebView2Async();
            Log.Write("login: CoreWebView2 ready");

            // Start logged-out every time so the next login is for a different alt.
            _web.CoreWebView2.CookieManager.DeleteAllCookies();
            _web.CoreWebView2.NavigationCompleted += async (_, _) => await TryCaptureAsync();

            _status.Text = "Log in to the account you want to add…";
            _web.CoreWebView2.Navigate("https://www.roblox.com/login");
            _poll.Start();
        }
        catch (Exception ex)
        {
            Log.Exception("login.InitAsync", ex);
            // Almost always: the WebView2 Runtime isn't installed.
            MessageBox.Show(this,
                "Couldn't start the in-app browser. The Microsoft Edge WebView2 Runtime is required " +
                "for in-app login.\n\nInstall it (free) from:\n" +
                "https://developer.microsoft.com/microsoft-edge/webview2/\n\n" +
                "Or use \"Paste cookie\" instead.\n\nDetails: " + ex.Message,
                "WebView2 not available", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }

    private async Task TryCaptureAsync()
    {
        if (_captured || _checking || _closing || IsDisposed || _web.CoreWebView2 is null) return;
        _checking = true;
        try
        {
            var cookies = await _web.CoreWebView2.CookieManager
                .GetCookiesAsync("https://www.roblox.com");
            if (_closing || IsDisposed) return; // user closed during the await

            CoreWebView2Cookie? sec = null;
            foreach (var c in cookies)
                if (string.Equals(c.Name, ".ROBLOSECURITY", StringComparison.OrdinalIgnoreCase))
                {
                    sec = c;
                    break;
                }

            string? value = sec?.Value;
            if (string.IsNullOrEmpty(value) || value.Length < 100)
                return; // not logged in yet

            // Source of truth: confirm the cookie actually authenticates. Pass the
            // cancellation token so closing the form aborts the in-flight request.
            RobloxUser? user;
            try { user = await _api.ValidateCookieAndGetUserAsync(value, _cts.Token); }
            catch (RobloxApiException) { return; }          // transient; the timer will retry
            catch (OperationCanceledException) { return; }  // form closing

            if (_closing || IsDisposed) return; // user closed during validation
            if (user is null) return;

            _captured = true;
            _poll.Stop();
            CapturedCookie = value;
            User = user;
            _status.Text = $"Logged in as {user.Name} — adding…";
            DialogResult = DialogResult.OK;
            Close();
        }
        catch
        {
            // Any transient error: leave it for the next poll tick.
        }
        finally
        {
            _checking = false;
        }
    }
}
