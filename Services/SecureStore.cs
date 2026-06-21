using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RobloxMultiManager.Models;

namespace RobloxMultiManager.Services;

/// <summary>
/// Persists the account list to %APPDATA%\RobloxMultiManager\accounts.json and
/// handles DPAPI encryption of the session cookie.
///
/// Security model:
///   * The .ROBLOSECURITY cookie is encrypted with Windows DPAPI scoped to the
///     CURRENT USER. The ciphertext is useless to any other user or machine.
///   * Plaintext cookies live in memory only for the few milliseconds it takes to
///     hit Roblox's auth API during a launch, then go out of scope.
///   * Nothing is ever sent anywhere except *.roblox.com.
/// </summary>
public sealed class SecureStore
{
    // A constant entropy salt mixed into DPAPI. Doesn't need to be secret — it just
    // means a blob from this app can't be trivially decrypted by an unrelated app
    // that happens to call DPAPI for the same user.
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("RobloxMultiManager::v1::cookie");

    private readonly string _dir;
    private readonly string _file;

    public SecureStore()
    {
        _dir = AppData.Dir; // %APPDATA%\Prism (migrated from the old name on first use)
        _file = Path.Combine(_dir, "accounts.json");
    }

    public string FilePath => _file;

    // ----- cookie encryption ---------------------------------------------------

    /// <summary>Encrypt a plaintext cookie -> base64 DPAPI blob for storage.</summary>
    public static string Protect(string plaintextCookie)
    {
        byte[] data = Encoding.UTF8.GetBytes(plaintextCookie);
        byte[] blob = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(blob);
    }

    /// <summary>Decrypt a stored base64 DPAPI blob -> plaintext cookie.</summary>
    public static string Unprotect(string encryptedBase64)
    {
        byte[] blob = Convert.FromBase64String(encryptedBase64);
        byte[] data = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(data);
    }

    // ----- persistence ---------------------------------------------------------

    /// <summary>
    /// Non-null if the last <see cref="Load"/> found an unreadable/corrupt file. The
    /// existing file was backed up rather than overwritten; surface this to the user.
    /// </summary>
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
            // Corrupt/unreadable file: preserve it under a backup name so the next
            // Save() can't silently overwrite a store the user might still recover,
            // then start empty rather than crash.
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

        // Write to a unique temp file, then ATOMICALLY swap it into place so a crash
        // or power loss mid-write can never truncate/corrupt the real list (File.Copy
        // would overwrite in place and is not atomic).
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
            // The temp holds encrypted cookies — don't leave it behind on failure.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }
}
