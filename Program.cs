using System.Windows.Forms;
using RobloxMultiManager.Services;
using RobloxMultiManager.UI;

namespace RobloxMultiManager;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // ---- bootstrap ----
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

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
            catch { }
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Exception("UnobservedTask", e.Exception);
            e.SetObserved();
        };
        Log.Write($"Prism started (v{typeof(Program).Assembly.GetName().Version}).");

        // ---- service wiring ----
        var store = new SecureStore();
        var api = new RobloxWebApi();
        var launcher = new RobloxLauncher(api);
        var accounts = new AccountManager(store, api, launcher);

        using (api)
        using (launcher)
        {
            Application.Run(new AppShell(accounts, launcher, api));
        }
    }
}
