namespace RobloxMultiManager.Services;

/// <summary>
/// Tiny append-only file logger at <c>%APPDATA%\Prism\logs\prism.log</c> (rotated at ~1 MB).
/// Used for crash diagnostics that users can export from Settings → About. Best-effort:
/// never throws. Holds no sensitive data (cookies are never passed here).
/// </summary>
public static class Log
{
    private static readonly object _gate = new();

    public static string Dir { get; } = Path.Combine(AppData.Dir, "logs");

    public static void Write(string message)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Dir);
                string path = Path.Combine(Dir, "prism.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                {
                    try { File.Delete(path + ".old"); } catch { }
                    try { File.Move(path, path + ".old"); } catch { }
                }
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }

    public static void Exception(string context, Exception ex) =>
        Write($"ERROR {context}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
}
