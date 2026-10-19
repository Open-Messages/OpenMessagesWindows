using System.Text.Json;

namespace OpenMessages.Core;

/// <summary>A decrypted code, ready to show.</summary>
public sealed record OtpItem(string Id, string Code, string Sender, string Body, DateTimeOffset ReceivedAt, DateTimeOffset? ExpiresAt);

/// <summary>The sync key as stored on this PC, tied to the account and salt it was derived for.</summary>
public sealed record StoredKey(string Uid, byte[] Salt, byte[] Key);

/// <summary>Encrypted-at-rest storage for the session and the sync key (DPAPI on Windows).</summary>
public interface ISecureStore
{
    T? Load<T>(string name);
    void Save<T>(string name, T value);
    void Delete(string name);
}

public enum SyncPhase
{
    MissingConfig,
    SignedOut,
    Loading,
    /// <summary>Signed in, but the phone hasn't created a sync key yet.</summary>
    NoKeyOnAccount,
    NeedsKey,
    Ready,
}

/// <summary>
/// Sign-in, key unlock and polling. Not thread-safe: call it from the UI thread, and its awaits
/// resume there, so <see cref="Changed"/> and <see cref="NewCode"/> are raised on the UI thread too.
/// </summary>
public sealed class OtpSyncService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    /// <summary>Every this many polls, re-read the key parameters and do a full reload.</summary>
    private const int FullReloadEvery = 60;

    private readonly ISecureStore _store;
    private readonly AppConfig? _config;
    private AuthSession? _session;
    private StoredKey? _storedKey;
    private RemoteKeyInfo? _keyInfo;
    private string? _cursor;
    private CancellationTokenSource? _poll;

    public SyncPhase Phase { get; private set; } = SyncPhase.Loading;
    public string? ConfigError { get; }
    public IReadOnlyList<OtpItem> Codes { get; private set; } = [];
    public string? Email => _session?.Email;
    public DateTimeOffset? LastSync { get; private set; }

    public event Action? Changed;
    public event Action<OtpItem>? NewCode;
    public event Action<string>? Error;

    public OtpSyncService(ISecureStore store, Func<AppConfig> loadConfig)
    {
        _store = store;
        try
        {
            _config = loadConfig();
        }
        catch (ConfigException e)
        {
            ConfigError = e.Message;
            Phase = SyncPhase.MissingConfig;
            return;
        }
        _session = store.Load<AuthSession>("session");
        _storedKey = store.Load<StoredKey>("syncKey");
    }

    public Task StartAsync() => _config == null ? Task.CompletedTask : LoadKeyStateAsync();

    // Account

    public async Task SignInAsync(CancellationToken ct)
    {
        if (_config == null) return;
        var googleToken = await new GoogleOAuth(_config).SignInAsync(ct);
        var session = await new FirebaseAuthClient(_config).SignInAsync(googleToken, ct);
        if (_storedKey?.Uid != session.Uid) ForgetKey();
        SaveSession(session);
        await LoadKeyStateAsync();
    }

    public void SignOut()
    {
        StopPolling();
        _store.Delete("session");
        ForgetKey();
        _session = null;
        Codes = [];
        SetPhase(SyncPhase.SignedOut);
    }

    // Sync key

    /// <summary>Returns false if the passphrase doesn't match the phone's key.</summary>
    public async Task<bool> UnlockAsync(string passphrase)
    {
        if (_session is not { } session || _keyInfo is not { } info) return false;
        var normalized = OtpCrypto.Normalize(passphrase);
        var key = await Task.Run(() => OtpCrypto.DeriveKey(normalized, info.Salt, info.Iterations));
        if (!OtpCrypto.Verify(key, info.Check, session.Uid)) return false;
        _storedKey = new StoredKey(session.Uid, info.Salt, key);
        _store.Save("syncKey", _storedKey);
        SetPhase(SyncPhase.Ready);
        await ReloadAsync();
        StartPolling();
        return true;
    }

    public void ForgetKey()
    {
        StopPolling();
        _store.Delete("syncKey");
        _storedKey = null;
        Codes = [];
        if (_session != null && Phase == SyncPhase.Ready) SetPhase(SyncPhase.NeedsKey);
        else Changed?.Invoke();
    }

    // Codes

    public async Task DeleteAsync(OtpItem item)
    {
        var session = await FreshSessionAsync();
        await new FirestoreClient(_config!).DeleteAsync(item.Id, session.IdToken);
        Codes = Codes.Where(c => c.Id != item.Id).ToList();
        Changed?.Invoke();
    }

    public async Task DeleteAllAsync()
    {
        var session = await FreshSessionAsync();
        var firestore = new FirestoreClient(_config!);
        foreach (var item in Codes.ToList())
        {
            await firestore.DeleteAsync(item.Id, session.IdToken);
            Codes = Codes.Where(c => c.Id != item.Id).ToList();
            Changed?.Invoke();
        }
    }

    public Task RefreshAsync() => LoadKeyStateAsync();

    // Internals

    /// <summary>Works out which phase we're in by checking the account's key on the server.</summary>
    private async Task LoadKeyStateAsync()
    {
        if (_config == null) return;
        if (_session == null)
        {
            SetPhase(SyncPhase.SignedOut);
            return;
        }
        try
        {
            var session = await FreshSessionAsync();
            var info = await new FirestoreClient(_config).GetKeyInfoAsync(session.Uid, session.IdToken);
            _keyInfo = info;
            if (info == null)
            {
                ForgetKey();
                SetPhase(SyncPhase.NoKeyOnAccount);
            }
            else if (_storedKey is { } stored && stored.Uid == session.Uid && stored.Salt.AsSpan().SequenceEqual(info.Salt))
            {
                SetPhase(SyncPhase.Ready);
                await ReloadAsync();
                StartPolling();
            }
            else
            {
                // The key was changed on the phone (new salt) or never entered here.
                ForgetKey();
                SetPhase(SyncPhase.NeedsKey);
            }
        }
        catch (ApiException e) when (e.Status is 400 or 401)
        {
            // Refresh token revoked or account deleted.
            SignOut();
            Error?.Invoke(e.Message);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ApiException)
        {
            if (Phase == SyncPhase.Loading) SetPhase(_storedKey == null ? SyncPhase.NeedsKey : SyncPhase.Ready);
            Error?.Invoke(e.Message);
        }
    }

    /// <summary>Replaces the list with the newest records.</summary>
    private async Task ReloadAsync()
    {
        _cursor = null;
        Codes = [];
        await FetchAsync(announce: false);
    }

    private async Task FetchAsync(bool announce)
    {
        if (_config == null || _storedKey is not { } stored) return;
        try
        {
            var session = await FreshSessionAsync();
            var records = await new FirestoreClient(_config).GetOtpsAsync(session.Uid, session.IdToken, _cursor);
            var known = Codes.Select(c => c.Id).ToHashSet();
            var aad = OtpCrypto.OtpAad(session.Uid);
            var fresh = new List<OtpItem>();
            foreach (var record in records.Where(r => !known.Contains(r.Name)))
            {
                try
                {
                    var payload = JsonSerializer.Deserialize<OtpPayload>(OtpCrypto.Decrypt(record.Sealed, stored.Key, aad));
                    if (payload == null) continue;
                    fresh.Add(new OtpItem(record.Name, payload.Code, payload.Sender, payload.Body,
                        DateTimeOffset.FromUnixTimeMilliseconds(payload.ReceivedAt), record.ExpiresAt));
                }
                catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or JsonException or FormatException)
                {
                    // Written with an older key, or corrupt: skip it.
                }
            }
            // Results are newest first and never older than the cursor, so the first one is the new cursor.
            if (records.Count > 0) _cursor = records[0].CreatedAt;
            var now = DateTimeOffset.UtcNow;
            Codes = fresh.Concat(Codes)
                .Where(c => (c.ExpiresAt ?? DateTimeOffset.MaxValue) > now)
                .OrderByDescending(c => c.ReceivedAt)
                .ToList();
            LastSync = now;
            Changed?.Invoke();
            if (announce && fresh.Count > 0) NewCode?.Invoke(fresh.MaxBy(c => c.ReceivedAt)!);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ApiException)
        {
            Error?.Invoke(e.Message);
        }
    }

    private void StartPolling()
    {
        StopPolling();
        var cts = new CancellationTokenSource();
        _poll = cts;
        _ = PollAsync(cts.Token);
    }

    private async Task PollAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval);
        var count = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (++count % FullReloadEvery == 0)
                {
                    await LoadKeyStateAsync(); // Restarts polling if the key is still valid.
                    return;
                }
                await FetchAsync(announce: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StopPolling()
    {
        _poll?.Cancel();
        _poll = null;
    }

    private async Task<AuthSession> FreshSessionAsync()
    {
        if (_config == null || _session == null) throw new ApiException(401, null);
        var fresh = await new FirebaseAuthClient(_config).RefreshedAsync(_session);
        if (fresh.IdToken != _session.IdToken) SaveSession(fresh);
        return fresh;
    }

    private void SaveSession(AuthSession session)
    {
        _store.Save("session", session);
        _session = session;
    }

    private void SetPhase(SyncPhase phase)
    {
        Phase = phase;
        Changed?.Invoke();
    }
}
