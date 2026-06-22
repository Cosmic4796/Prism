namespace RobloxMultiManager.Services;

public static class AppData
{
    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Prism");

    static AppData()
    {
        try
        {
            string legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RobloxMultiManager");

            if (Directory.Exists(legacy) && !Directory.Exists(Dir))
                Directory.Move(legacy, Dir);
        }
        catch { }

        try { Directory.CreateDirectory(Dir); } catch { }
    }
}
