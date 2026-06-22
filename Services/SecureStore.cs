using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RobloxMultiManager.Models;

namespace RobloxMultiManager.Services;

public sealed class SecureStore
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("RobloxMultiManager::v1::cookie");

    private readonly string _dir;
    private readonly string _file;

    public SecureStore()
    {
        _dir = AppData.Dir;
        _file = Path.Combine(_dir, "accounts.json");
    }

    public string FilePath => _file;

    // ---- cookie encryption ----

    public static string Protect(string plaintextCookie)
    {
        byte[] data = Encoding.UTF8.GetBytes(plaintextCookie);
        byte[] blob = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(blob);
    }

    public static string Unprotect(string encryptedBase64)
    {
        byte[] blob = Convert.FromBase64String(encryptedBase64);
        byte[] data = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(data);
    }

    // ---- persistence ----

    public string? LoadError { get; private set; }

    public List<Account> Load()
    {
        LoadError = null;
        try
        {
            if (!File.Exists(_file)) return new List<Account>();
            string json = File.ReadAllText(_file);
            return JsonSerializer.Deserialize<List<Account>>(json) ?? new List<Account>();
        }
        catch (Exception ex)
        {
            try
            {
                string backup = _file + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                if (File.Exists(_file)) File.Copy(_file, backup, overwrite: true);
                LoadError =
                    $"Couldn't read your saved accounts ({ex.Message}). " +
                    $"The file was backed up as {Path.GetFileName(backup)} and the list started empty.";
            }
            catch
            {
                LoadError = $"Couldn't read your saved accounts ({ex.Message}). The list started empty.";
            }
            return new List<Account>();
        }
    }

    public void Save(IEnumerable<Account> accounts)
    {
        Directory.CreateDirectory(_dir);
        string json = JsonSerializer.Serialize(
            accounts, new JsonSerializerOptions { WriteIndented = true });

        string tmp = Path.Combine(_dir, Path.GetRandomFileName());
        File.WriteAllText(tmp, json);
        try
        {
            if (File.Exists(_file))
                File.Replace(tmp, _file, destinationBackupFileName: null);
            else
                File.Move(tmp, _file);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }
}
