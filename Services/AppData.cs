namespace RobloxMultiManager.Services;

/// <summary>
/// Central location for Prism's roaming data (accounts, settings, stats), i.e.
/// <c>%APPDATA%\Prism</c>. The folder was historically named "RobloxMultiManager";
/// it's renamed to "Prism" for the public release, and existing installs are migrated
/// once on first launch so nobody loses their accounts when they update.
/// </summary>
public static class AppData
{
    /// <summary><c>%APPDATA%\Prism</c> — guaranteed to exist after the type is first used.</summary>
    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Prism");

    static AppData()
    {
        try
        {
            string legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RobloxMultiManager");

            // One-time rename of the old data folder. Only when the new folder doesn't
            // exist yet, so we never clobber a fresh install or an already-migrated one.
            if (Directory.Exists(legacy) && !Directory.Exists(Dir))
                Directory.Move(legacy, Dir);
        }
        catch { /* best effort — fall back to a fresh Prism folder */ }

        try { Directory.CreateDirectory(Dir); } catch { }
    }
}
