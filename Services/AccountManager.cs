using RobloxMultiManager.Models;

namespace RobloxMultiManager.Services;

public sealed class AccountManager
{
    private readonly SecureStore _store;
    private readonly RobloxWebApi _api;
    private readonly RobloxLauncher _launcher;
    private readonly object _lock = new();
    private readonly List<Account> _accounts;

    public string? LoadWarning { get; }

    public AccountManager(SecureStore store, RobloxWebApi api, RobloxLauncher launcher)
    {
        _store = store;
        _api = api;
        _launcher = launcher;
        _accounts = _store.Load();
        LoadWarning = _store.LoadError;

        _api.CookieRotated += OnCookieRotated;
    }

    public IReadOnlyList<Account> Snapshot()
    {
        lock (_lock) return _accounts.ToList();
    }

    public int Count
    {
        get { lock (_lock) return _accounts.Count; }
    }

    // ---- account crud ----

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

    // ---- launching ----

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

    // ---- status and lookups ----

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

    public Task<Dictionary<long, string>> GetPlaceNamesAsync(
        Account account, IEnumerable<long> placeIds, CancellationToken ct = default)
    {
        string cookie = DecryptCookie(account);
        return _api.GetPlaceNamesAsync(cookie, placeIds, ct);
    }

    public Task<string?> ResolvePrivateAccessCodeAsync(
        Account account, long placeId, string linkCode, CancellationToken ct = default)
    {
        string cookie = DecryptCookie(account);
        return _api.ResolvePrivateServerAccessCodeAsync(cookie, placeId, linkCode, ct);
    }

    public Task<ShareLinkResult?> ResolveShareLinkAsync(Account account, string code, CancellationToken ct = default)
    {
        string cookie = DecryptCookie(account);
        return _api.ResolveShareLinkAsync(cookie, code, ct);
    }

    // ---- cookie handling ----

    private string DecryptCookie(Account account)
    {
        try
        {
            return SecureStore.Unprotect(account.EncryptedCookie);
        }
        catch (Exception ex)
        {
            throw new RobloxApiException(
                $"Couldn't decrypt \"{account.Alias}\"'s cookie. The account list is bound to this " +
                $"Windows user on this PC and can't be moved. Re-add the account. ({ex.Message})", ex);
        }
    }

    private void OnCookieRotated(string oldCookie, string newCookie)
    {
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
                        account.EncryptedCookie = SecureStore.Protect(newCookie);
                        changed = true;
                    }
                }

                if (changed)
                {
                    try { _store.Save(_accounts); } catch { }
                }
            }
        }
        catch { }
    }
}
