using System.Windows.Forms;
using RobloxMultiManager.Services;
using RobloxMultiManager.UI;

namespace RobloxMultiManager;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Safety net: show, don't crash, on any exception that slips past a handler
        // (e.g. an unexpected one escaping an async void event handler).
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Exception("ThreadException", e.Exception);
            MessageBox.Show(e.Exception.Message, "Unexpected error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                Log.Exception("UnhandledException", e.ExceptionObject as Exception ?? new Exception("Unknown error"));
                MessageBox.Show((e.ExceptionObject as Exception)?.Message ?? "Unknown error",
                    "Unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { /* nothing more we can do */ }
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Exception("UnobservedTask", e.Exception);
            e.SetObserved();
        };
        Log.Write($"Prism started (v{typeof(Program).Assembly.GetName().Version}).");

        // Wire up the services. Plaintext cookies live only inside AccountManager /
        // RobloxWebApi for the moment of a request; on disk they're DPAPI-encrypted.
        var store = new SecureStore();
        var api = new RobloxWebApi();
        var launcher = new RobloxLauncher(api);
        var accounts = new AccountManager(store, api, launcher);

        // MainForm.OnLoad acquires the single-instance lock (idempotently) and reports
        // status. launcher.Dispose() releases it on exit.
        using (api)
        using (launcher)
        {
            Application.Run(new AppShell(accounts, launcher, api));
        }
    }
}
