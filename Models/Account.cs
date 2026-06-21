namespace RobloxMultiManager.Models;

// ---- account model ----
public sealed class Account
{
    public string Alias { get; set; } = "";

    public string EncryptedCookie { get; set; } = "";

    public long? UserId { get; set; }

    public string? Username { get; set; }

    public string? Note { get; set; }

    public string AddedUtc { get; set; } = "";

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Username) ? Alias : $"{Username}  ({Alias})";
}
