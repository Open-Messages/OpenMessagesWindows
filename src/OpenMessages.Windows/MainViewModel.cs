using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using OpenMessages.Core;

namespace OpenMessages.Windows;

/// <summary>Binds <see cref="OtpSyncService"/> to the window and tray.</summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly OtpSyncService _sync;
    private readonly UserSettings _settings = UserSettings.Load();
    private CancellationTokenSource? _signIn;
    private bool _busy;
    private string? _status;

    public MainViewModel(OtpSyncService sync)
    {
        _sync = sync;
        _sync.Changed += Refresh;
        _sync.Error += message => Status = message;
        _sync.NewCode += OnNewCode;

        SignInCommand = new RelayCommand(_ => Run(SignInAsync), _ => !Busy);
        CancelSignInCommand = new RelayCommand(() => _signIn?.Cancel());
        SignOutCommand = new RelayCommand(_sync.SignOut);
        RefreshCommand = new RelayCommand(_ => Run(_sync.RefreshAsync), _ => !Busy);
        ForgetKeyCommand = new RelayCommand(_sync.ForgetKey);
        CopyCommand = new RelayCommand(p => { if (p is OtpItem item) Copy(item); });
        DeleteCommand = new RelayCommand(p => { if (p is OtpItem item) Run(() => _sync.DeleteAsync(item)); });
        DeleteAllCommand = new RelayCommand(_ => Run(_sync.DeleteAllAsync), _ => Codes.Count > 0 && !Busy);
    }

    public ObservableCollection<OtpItem> Codes { get; } = [];
    public SyncPhase Phase => _sync.Phase;
    public string? ConfigError => _sync.ConfigError;
    public string Email => _sync.Email ?? "";
    public string LastSync => _sync.LastSync is { } t ? $"Updated {t.ToLocalTime():T}" : "";

    public bool Busy
    {
        get => _busy;
        private set => Set(ref _busy, value);
    }

    /// <summary>The last error, shown in a dismissible bar.</summary>
    public string? Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public bool AutoCopy
    {
        get => _settings.AutoCopy;
        set { _settings.AutoCopy = value; _settings.Save(); OnPropertyChanged(); }
    }

    public bool Notify
    {
        get => _settings.Notify;
        set { _settings.Notify = value; _settings.Save(); OnPropertyChanged(); }
    }

    public bool StartWithWindows
    {
        get => UserSettings.StartWithWindows;
        set { UserSettings.StartWithWindows = value; OnPropertyChanged(); }
    }

    public RelayCommand SignInCommand { get; }
    public RelayCommand CancelSignInCommand { get; }
    public RelayCommand SignOutCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ForgetKeyCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand DeleteAllCommand { get; }

    /// <summary>Raised for each new code when notifications are on; the tray shows it.</summary>
    public event Action<OtpItem, bool>? Announce;

    public async Task<bool> UnlockAsync(string passphrase)
    {
        Busy = true;
        try
        {
            var ok = await _sync.UnlockAsync(passphrase);
            if (!ok) Status = "That key doesn't match the one set on your phone.";
            return ok;
        }
        finally
        {
            Busy = false;
        }
    }

    public void Copy(OtpItem item) => ClipboardHelper.Copy(item.Code, sensitive: true);

    private async Task SignInAsync()
    {
        _signIn = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await _sync.SignInAsync(_signIn.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "Sign-in was cancelled.";
        }
        finally
        {
            _signIn = null;
        }
    }

    private void OnNewCode(OtpItem item)
    {
        if (AutoCopy) Copy(item);
        if (Notify) Announce?.Invoke(item, AutoCopy);
    }

    private async void Run(Func<Task> work)
    {
        Busy = true;
        Status = null;
        try
        {
            await work();
        }
        catch (Exception e)
        {
            Status = e.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    private void Refresh()
    {
        Codes.Clear();
        foreach (var code in _sync.Codes) Codes.Add(code);
        OnPropertyChanged(nameof(Phase));
        OnPropertyChanged(nameof(Email));
        OnPropertyChanged(nameof(LastSync));
        CodesChanged?.Invoke();
    }

    /// <summary>The tray rebuilds its menu from this.</summary>
    public event Action? CodesChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }
}
