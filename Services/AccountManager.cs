using RobloxMultiManager.Models;

namespace RobloxMultiManager.Services;

/// <summary>
/// The in-memory account list and the operations the UI calls: add (with cookie
/// validation), remove, rename, refresh, and launch. Plaintext cookies never leave
/// this class except as the Cookie header inside <see cref="RobloxWebApi"/> / a
/// launch ticket request — on disk they're always DPAPI-encrypted via
/// <see cref="SecureStore"/>.
/// </summary>
public sealed class AccountManager
{
    private readonly SecureStore _store;
    private readonly RobloxWebApi _api;
    private readonly RobloxLauncher _launcher;
    private readonly object _lock = new();
    private readonly List<Account> _accounts;

    /// <summary>Set if the saved account file couldn't be read on startup (it was
    /// backed up, not overwritten). The UI should show this once.</summary>
    public string? LoadWarning { get; }

    public AccountManager(SecureStore store, RobloxWebApi api, RobloxLauncher launcher)
    {
        _store = store;
        _api = api;
        _launcher = launcher;
        _accounts = _store.Load();
        LoadWarning = _store.LoadError;

        // Persist Roblox's rotated cookies (mandatory since the 2026-05-01 cookie
        // format change) so accounts don't silently start failing with 401.
        _api.CookieRotated += OnCookieRotated;
    }

    /// <summary>A snapshot of the current accounts (safe to enumerate on the UI thread).</summary>
    public IReadOnlyList<Account> Snapshot()
    {
        lock (_lock) return _accounts.ToList();
    }

    public int Count
    {
        get { lock (_lock) return _accounts.Count; }
    }

    /// <summary>
    /// Validates the pasted cookie against Roblox, then stores the account encrypted.
    /// Throws <see cref="RobloxApiException"/> if the cookie is invalid or already added.
    /// </summary>
    public async Task<Account> AddAccountAsync(
        string alias, string plaintextCookie, string? note, CancellationToken ct = default)
    {
        string cookie = (plaintextCookie ?? "").Trim();
        if (cookie.Length == 0)
            throw new RobloxApiException("Paste the account's .ROBLOSECURITY cookie.");

        RobloxUser? user = await _api.ValidateCookieAndGetUserAsync(cookie, ct).ConfigureAwait(false);
        if (user is null)
            throw new RobloxApiException("That cookie is invalid or expired — Roblox rejected it (401).");

        lock (_lock)
        {
            if (_accounts.Any(a => a.UserId == user.Id))
            {
                string existing = _accounts.First(a => a.UserId == user.Id).Alias;
                throw new RobloxApiException(
                    $"{user.Name} is already added (as \"{existing}\").");
            }

            var account = new Account
            {
                Alias = string.IsNullOrWhiteSpace(alias) ? user.Name : alias.Trim(),
                EncryptedCookie = SecureStore.Protect(cookie),
                UserId = user.Id,
                Username = user.Name,
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                AddedUtc = DateTime.UtcNow.ToString("o"),
            };
            _accounts.Add(account);
            _store.Save(_accounts);
            return account;
        }
    }

    public void Remove(Account account)
    {
        lock (_lock)
        {
            if (_accounts.Remove(account))
                _store.Save(_accounts);
        }
    }

    public void Rename(Account account, string newAlias)
    {
        newAlias = (newAlias ?? "").Trim();
        if (newAlias.Length == 0) return;
        lock (_lock)
        {
            account.Alias = newAlias;
            _store.Save(_accounts);
        }
    }

    /// <summary>
    /// Re-validates the stored cookie and refreshes the cached username/id.
    /// Returns false if the cookie is no longer valid.
    /// </summary>
    public async Task<bool> RefreshAsync(Account account, CancellationToken ct = default)
    {
        string cookie = DecryptCookie(account);
        RobloxUser? user = await _api.ValidateCookieAndGetUserAsync(cookie, ct).ConfigureAwait(false);
        if (user is null) return false;

        lock (_lock)
        {
            account.UserId = user.Id;
            account.Username = user.Name;
            _store.Save(_accounts);
        }
        return true;
    }

    /// <summary>Mints a fresh ticket and launches this account into the chosen place/server.</summary>
    public Task LaunchAsync(
        Account account,
        long placeId,
        string? jobId = null,
        string? privateServerAccessCode = null,
        string? privateServerLinkCode = null,
        CancellationToken ct = default)
    {
        string cookie = DecryptCookie(account);
        return _launcher.LaunchForAccountAsync(
            cookie, placeId, jobId, privateServerAccessCode, privateServerLinkCode, ct);
    }

    /// <summary>
    /// Re-validates the cookie (refreshing username/id + persisting any rotated cookie),
    /// then fetches Robux + Premium. Returns ok=false if the cookie is dead.
    /// </summary>
    public async Task<(bool ok, long? robux, bool? premium)> GetAccountStatusAsync(Account account, CancellationToken ct = default)
    {
        string cookie = DecryptCookie(account);
        var user = await _api.ValidateCookieAndGetUserAsync(cookie, ct).ConfigureAwait(false);
        if (user is null) return (false, null, null);

        lock (_lock)
        {
            account.UserId = user.Id;
            account.Username = user.Name;
            _store.Save(_accounts);
        }
        long? robux = await _api.GetRobuxAsync(cookie, user.Id, ct).ConfigureAwait(false);
        bool? premium = await _api.GetPremiumAsync(cookie, user.Id, ct).ConfigureAwait(false);
        return (true, robux, premium);
    }

    /// <summary>Best-effort place-name lookup via this account's session (for Analytics).</summary>
    public Task<Dictionary<long, string>> GetPlaceNamesAsync(
        Account account, IEnumerable<long> placeIds, CancellationToken ct = default)
    {
        string cookie = DecryptCookie(account);
        return _api.GetPlaceNamesAsync(cookie, placeIds, ct);
    }

    /// <summary>Resolves a private-server linkCode to its accessCode using this account's session.</summary>
    public Task<string?> ResolvePrivateAccessCodeAsync(
        Account account, long placeId, string linkCode, CancellationToken ct = default)
    {
        string cookie = DecryptCookie(account);
        return _api.ResolvePrivateServerAccessCodeAsync(cookie, placeId, linkCode, ct);
    }

    /// <summary>Resolves a new-format Roblox share-link code into place + server info using this account's session.</summary>
    public Task<ShareLinkResult?> ResolveShareLinkAsync(Account account, string code, CancellationToken ct = default)
    {
        string cookie = DecryptCookie(account);
        return _api.ResolveShareLinkAsync(cookie, code, ct);
    }

    private string DecryptCookie(Account account)
    {
        try
        {
            return SecureStore.Unprotect(account.EncryptedCookie);
        }
        catch (Exception ex)
        {
            // DPAPI fails if the file was copied from another machine/user.
            throw new RobloxApiException(
                $"Couldn't decrypt \"{account.Alias}\"'s cookie. The account list is bound to this " +
                $"Windows user on this PC and can't be moved. Re-add the account. ({ex.Message})", ex);
        }
    }

    private void OnCookieRotated(string oldCookie, string newCookie)
    {
        // This fires synchronously inside an API call (possibly on a background
        // thread). It must never throw back into that request.
        try
        {
            lock (_lock)
            {
                bool changed = false;
                foreach (var account in _accounts)
                {
                    string current;
                    try { current = SecureStore.Unprotect(account.EncryptedCookie); }
                    catch { continue; }

                    if (current == oldCookie)
                    {
                        // Update in memory FIRST so the live session keeps working even
                        // if persisting to disk fails.
                        account.EncryptedCookie = SecureStore.Protect(newCookie);
                        changed = true;
                    }
                }

                if (changed)
                {
                    // If the save fails (disk full, file locked), keep the in-memory
                    // rotation; the next account mutation will re-persist it.
                    try { _store.Save(_accounts); } catch { /* best effort */ }
                }
            }
        }
        catch { /* never disturb the request that triggered the rotation */ }
    }
}
