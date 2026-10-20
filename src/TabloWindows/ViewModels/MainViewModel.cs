using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using TabloWindows.Models;
using TabloWindows.Services;

namespace TabloWindows.ViewModels;

public enum Section { Recordings, LiveTv, MultiView, Guide, Exports }

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly LibVLC _libvlc;
    private readonly TabloClient _client = new();
    private readonly CredentialStore _creds = new();
    private readonly ExportService _exportService;

    // ---- top-level state ----
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _loginError = "";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private bool _rememberMe = true;
    [ObservableProperty] private string _deviceName = "";
    [ObservableProperty] private Section _currentSection = Section.Recordings;
    [ObservableProperty] private bool _playerOpen;

    /// <summary>
    /// The Mute button (or M). Not saved: every launch starts with sound, so a mute left on
    /// can never pass for an app that has lost its audio.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteText))]
    private bool _isMuted;

    public string MuteText => IsMuted ? "🔇  Unmute" : "🔊  Mute";

    partial void OnIsMutedChanged(bool value)
    {
        Player.SetMuted(value);
        MultiView.SetMuted(value);
    }

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    // ---- device selection ----
    public ObservableCollection<TabloDevice> Devices { get; } = new();
    [ObservableProperty] private TabloDevice? _selectedDevice;

    // ---- child VMs ----
    public RecordingsViewModel Recordings { get; }
    public LiveTvViewModel LiveTv { get; }
    public GuideViewModel Guide { get; }
    public MultiViewViewModel MultiView { get; }
    public ExportsViewModel Exports { get; }
    public PlayerViewModel Player { get; }
    public UpdateService Updater { get; } = new();

    public bool IsRecordings => CurrentSection == Section.Recordings;
    public bool IsLiveTv => CurrentSection == Section.LiveTv;
    public bool IsGuide => CurrentSection == Section.Guide;
    public bool IsMultiView => CurrentSection == Section.MultiView;
    public bool IsExports => CurrentSection == Section.Exports;

    public MainViewModel()
    {
        App.Trace("MainViewModel ctor: creating LibVLC");
        _libvlc = new LibVLC();
        App.Trace("LibVLC created");
        _exportService = new ExportService(_libvlc, _client);
        Player = new PlayerViewModel(_libvlc, _client);
        Exports = new ExportsViewModel(_exportService);
        Recordings = new RecordingsViewModel(_client, PlayAsync, Exports.EnqueueAsync);
        LiveTv = new LiveTvViewModel(_client, PlayAsync);
        Guide = new GuideViewModel(_client, PlayAsync);
        MultiView = new MultiViewViewModel(_libvlc, _client, LiveTv);
        // Both hold tuners: starting multi-view closes an open player first.
        MultiView.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MultiViewViewModel.IsOpen) && MultiView.IsOpen && PlayerOpen)
                ClosePlayer();
        };

        var saved = _creds.Load();
        if (saved is not null)
        {
            Email = saved.Email;
            SavedPassword = saved.Password;   // code-behind pre-fills the PasswordBox with this
        }
    }

    /// <summary>Remembered password, used to pre-fill the PasswordBox (PasswordBox can't bind).</summary>
    public string? SavedPassword { get; }

    partial void OnCurrentSectionChanged(Section value)
    {
        OnPropertyChanged(nameof(IsRecordings));
        OnPropertyChanged(nameof(IsLiveTv));
        OnPropertyChanged(nameof(IsGuide));
        OnPropertyChanged(nameof(IsMultiView));
        OnPropertyChanged(nameof(IsExports));
    }

    // ---- login flow (password passed in from the PasswordBox in code-behind) ----

    /// <summary>On startup, silently sign in if we have remembered credentials.</summary>
    public async Task TryAutoLoginAsync()
    {
        var saved = _creds.Load();
        if (saved is null || string.IsNullOrEmpty(saved.Password)) return;
        Email = saved.Email;
        RememberMe = true;
        await LoginAsync(saved.Password);
    }

    public async Task LoginAsync(string password)
    {
        LoginError = "";
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrEmpty(password))
        {
            LoginError = "Enter your Tablo account email and password.";
            return;
        }
        IsBusy = true;
        try
        {
            App.Trace("LoginAsync: calling cloud login");
            await _client.LoginAsync(Email.Trim(), password);
            App.Trace("LoginAsync: cloud login OK");
            var all = _client.Account!.Devices;
            var saved = _creds.Load();
            var preferred = saved?.PreferredServerId;

            // Probe every device on the account in parallel; the account keeps listing dead/
            // returned units, so connect only to one that's actually online on the LAN.
            App.Trace($"LoginAsync: probing {all.Count} device(s) for reachability");
            var probes = await Task.WhenAll(all.Select(async d =>
                (device: d, info: await TabloClient.ProbeAsync(d))));
            var reachable = probes.Where(p => p.info is not null).Select(p => p.device).ToList();
            App.Trace($"LoginAsync: {reachable.Count} reachable");

            // Show only working devices when we found any; otherwise show all so the user can retry.
            var listed = reachable.Count > 0 ? reachable : all.ToList();
            Devices.Clear();
            foreach (var d in listed) Devices.Add(d);

            SelectedDevice =
                Devices.FirstOrDefault(d => d.ServerId == preferred)   // remembered, if still online
                ?? Devices.FirstOrDefault();

            if (RememberMe)
                _creds.Save(new CredentialStore.Creds(Email.Trim(), password, SelectedDevice?.ServerId));
            else
                _creds.Clear();

            if (reachable.Count == 0)
            {
                // Auto-detect found nothing (offline, or a proxy/firewall blocking the probe).
                // Don't hard-block — let the user pick a device and connect to it directly.
                LoginError = "Couldn't auto-detect an online Tablo. Pick your device below and press Connect.";
                return;
            }

            await ConnectSelectedDeviceAsync();
        }
        catch (TabloAuthException ex) { LoginError = ex.Message; }
        catch (Exception ex) { LoginError = "Login failed: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ConnectSelectedDevice()
    {
        await ConnectSelectedDeviceAsync();
    }

    private async Task ConnectSelectedDeviceAsync()
    {
        if (SelectedDevice is null) { LoginError = "No Tablo devices on this account."; return; }
        IsBusy = true;
        try
        {
            var profile = _client.Account!.Profiles.First();
            App.Trace($"ConnectSelectedDevice: selecting {SelectedDevice.ServerId}");
            await _client.SelectDeviceAsync(profile, SelectedDevice);
            App.Trace("ConnectSelectedDevice: device selected; loading recordings");
            DeviceName = _client.ServerInfo?.Model.Name ?? SelectedDevice.Name;

            // persist chosen device
            var saved = _creds.Load();
            if (saved is not null)
                _creds.Save(saved with { PreferredServerId = SelectedDevice.ServerId });

            IsLoggedIn = true;
            CurrentSection = Section.Recordings;
            await Recordings.LoadAsync();
        }
        catch (Exception ex)
        {
            LoginError = $"Could not reach “{SelectedDevice.Name}”. Make sure it's powered on and on this network.\n{ex.Message}";
            IsLoggedIn = false;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Logout()
    {
        Player.Stop();
        PlayerOpen = false;
        MultiView.Stop();
        IsLoggedIn = false;
        Devices.Clear();
        _creds.Clear();
    }

    // ---- navigation ----
    [RelayCommand] private void ShowRecordings() => CurrentSection = Section.Recordings;
    [RelayCommand]
    private async Task ShowLiveTv()
    {
        CurrentSection = Section.LiveTv;
        if (LiveTv.Channels.Count == 0) await LiveTv.LoadAsync();
    }
    [RelayCommand]
    private async Task ShowGuide()
    {
        CurrentSection = Section.Guide;
        if (Guide.Rows.Count == 0) await Guide.LoadAsync();
    }
    [RelayCommand]
    private async Task ShowMultiView()
    {
        CurrentSection = Section.MultiView;
        await MultiView.LoadAsync();
    }
    [RelayCommand] private void ShowExports() => CurrentSection = Section.Exports;
    [RelayCommand] private void RefreshRecordings() => _ = Recordings.LoadAsync();

    [RelayCommand]
    private void ClosePlayer()
    {
        _channelTimer?.Stop();
        _pendingChannel = null;
        Player.Stop();
        PlayerOpen = false;
    }

    // ---- channel up/down while watching live ----

    // Where Up/Down has moved to but not tuned yet. Flicking past five channels should tune once,
    // not five times - each tune holds a tuner until the Tablo notices nobody is watching it.
    private ChannelItem? _pendingChannel;
    private System.Windows.Threading.DispatcherTimer? _channelTimer;
    private const int ChannelSettleMs = 700;

    /// <summary>
    /// Up/Down on a live channel: step through the Live TV list (antenna and free streaming
    /// channels kept apart, wrapping at the ends). The heading changes at once; the tune waits
    /// until the presses stop, or until <see cref="ConfirmChannel"/>.
    /// </summary>
    public async Task StepChannelAsync(int delta)
    {
        if (!PlayerOpen || !Player.IsLive) return;
        if (LiveTv.Channels.Count == 0) await LiveTv.LoadAsync();

        var from = _pendingChannel?.Path ?? Player.CurrentPath;
        var current = LiveTv.Channels.FirstOrDefault(c => c.Path == from);
        var fast = current?.IsFast ?? Services.TabloClient.IsFast(from);
        var list = LiveTv.Channels.Where(c => c.IsFast == fast).ToList();
        if (list.Count == 0) return;
        var index = list.FindIndex(c => c.Path == from);
        var next = list[index < 0 ? 0 : ((index + delta) % list.Count + list.Count) % list.Count];

        _pendingChannel = next;
        Player.Title = next.WatchTitle;
        Player.BehindLive = "";

        if (_channelTimer is null)
        {
            _channelTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(ChannelSettleMs)
            };
            _channelTimer.Tick += async (_, _) => await TunePendingChannelAsync();
        }
        _channelTimer.Stop();
        _channelTimer.Start();
    }

    /// <summary>True while Up/Down has picked a channel that is not tuned yet.</summary>
    public bool ChannelChangePending => _pendingChannel is not null;

    /// <summary>Enter during a channel change: go there now instead of waiting out the settle.</summary>
    public Task ConfirmChannelAsync() => TunePendingChannelAsync();

    private async Task TunePendingChannelAsync()
    {
        _channelTimer?.Stop();
        if (_pendingChannel is not { } target || !PlayerOpen) return;
        _pendingChannel = null;
        await Player.PlayAsync(target.Path, target.WatchTitle, true);
    }

    // ---- playback bridge passed to child VMs ----
    private async Task PlayAsync(string path, string title, bool live)
    {
        MultiView.Stop();
        PlayerOpen = true;
        await Player.PlayAsync(path, title, live);
    }

    public void Dispose()
    {
        MultiView.Dispose();
        Player.Dispose();
        _libvlc.Dispose();
    }
}
