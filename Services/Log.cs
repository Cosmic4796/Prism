namespace RobloxMultiManager.Services;

public static class Log
{
    private static readonly object _gate = new();

    public static string Dir { get; } = Path.Combine(AppData.Dir, "logs");

    // ---- write ----
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
        catch { }
    }

    public static void Exception(string context, Exception ex) =>
        Write($"ERROR {context}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
}
