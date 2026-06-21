namespace RobloxMultiManager.Models;

/// <summary>
/// One saved Roblox account. The only sensitive field — the .ROBLOSECURITY
/// session cookie — is never stored in the clear: <see cref="EncryptedCookie"/>
/// holds a DPAPI (CurrentUser) blob, base64-encoded. It can only be decrypted by
/// the same Windows user on the same machine. Everything else is harmless metadata.
/// </summary>
public sealed class Account
{
    /// <summary>Friendly name you pick (e.g. "MainAlt", "TradeMule").</summary>
    public string Alias { get; set; } = "";

    /// <summary>Base64 of the DPAPI-protected .ROBLOSECURITY cookie. Never plaintext.</summary>
    public string EncryptedCookie { get; set; } = "";

    /// <summary>Roblox user id, fetched once on add for display/sanity. Not secret.</summary>
    public long? UserId { get; set; }

    /// <summary>Roblox username, fetched on add/refresh for display. Not secret.</summary>
    public string? Username { get; set; }

    /// <summary>Optional free-text note ("has the limited", etc.).</summary>
    public string? Note { get; set; }

    /// <summary>When the account was added (UTC, ISO-8601).</summary>
    public string AddedUtc { get; set; } = "";

    /// <summary>Display label for lists: "Username (Alias)" or just the alias.</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Username) ? Alias : $"{Username}  ({Alias})";
}
