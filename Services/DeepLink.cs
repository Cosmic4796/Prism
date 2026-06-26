using System.IO;
using System.IO.Pipes;
using System.Threading;
using Microsoft.Win32;

namespace RobloxMultiManager.Services;

// ---- prism:// deep links + single instance ----
public static class DeepLink
{
    public const string Scheme = "prism";
    private const string PipeName = "Prism_DeepLink_pipe";
    private const string MutexName = "Prism_SingleInstance_mtx";

    public static string? UrlFromArgs(string[] args)
    {
        foreach (var a in args)
            if (!string.IsNullOrEmpty(a) && a.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase)) return a.Trim();
        return null;
    }

    private static string ExePath() => Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;

    public static void RegisterProtocol()
    {
        try
        {
            string exe = ExePath();
            string want = $"\"{exe}\" \"%1\"";
            using (var existing = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + Scheme + @"\shell\open\command"))
                if (existing?.GetValue(null) as string == want) return; // already current, skip the write
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Scheme);
            key.SetValue(null, "URL:Prism Protocol");
            key.SetValue("URL Protocol", "");
            using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue(null, exe + ",0");
            using (var cmd = key.CreateSubKey(@"shell\open\command")) cmd.SetValue(null, want);
        }
        catch { }
    }

    // returns the held Mutex if we are the first/primary instance, else null
    public static Mutex? TryBecomePrimary()
    {
        var m = new Mutex(true, MutexName, out bool created);
        if (created) return m;
        m.Dispose();
        return null;
    }

    // secondary instance -> hand the payload (a prism:// url, or "focus") to the running primary
    public static void SendToPrimary(string payload)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(2000);
            using var w = new StreamWriter(pipe) { AutoFlush = true };
            w.WriteLine(payload);
        }
        catch { }
    }

    // primary instance -> listen for handoffs and invoke onMessage for each
    public static void StartServer(Action<string> onMessage)
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                    server.WaitForConnection();
                    using var r = new StreamReader(server);
                    string? line = r.ReadLine();
                    if (!string.IsNullOrEmpty(line)) onMessage(line);
                }
                catch { Thread.Sleep(500); }
            }
        })
        { IsBackground = true, Name = "PrismDeepLink" };
        t.Start();
    }
}
