using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using TabloWindows.Services;
using Timer = System.Timers.Timer;

namespace TabloWindows.ViewModels;

public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly LibVLC _libvlc;
    private readonly TabloClient _client;

    public MediaPlayer MediaPlayer { get; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _statusMessage = "";
    /// <summary>"12:34 behind live" while a paused or rewound live channel trails the broadcast;
    /// empty when watching live.</summary>
    [ObservableProperty] private string _behindLive = "";

    /// <summary>The Tablo path playing now (a channel or recording); empty when nothing is.</summary>
    public string CurrentPath => _path;

    // ---- live pause / skip state ----
    // VLC reports a live channel's Length (everything the Tablo has buffered since tune) but only
    // refreshes it every ~20 seconds, and not at all while paused, so the live edge is estimated
    // as the last Length plus the wall-clock time since it last moved.
    private long _lastLength;
    private DateTime _lengthChangedUtc;
    // Playing at live still trails that edge by ~10-13s (network cache + segment latency). The
    // smallest gap seen this session is taken as "live"; anything beyond it is time behind.
    private long _liveGapMs = long.MaxValue;
    // Where a live pause froze. On resume VLC skips forward toward live on its own (measured:
    // a 14s pause resumed ~18s further into the programme), so it is put back here explicitly.
    private long _pausedAtMs = -1;

    public string LoadingText => string.IsNullOrEmpty(StatusMessage) ? "Buffering…" : StatusMessage;
    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(LoadingText));

    // ---- auto-recovery state ----
    private string _path = "";
    private bool _userPaused;
    private int _recoveringFlag;     // 0/1, guarded with Interlocked
    private int _recoverAttempts;
    private long _lastTime = -1;
    private DateTime _lastAdvanceUtc;
    private readonly Timer _watchdog;
    private const int StallSeconds = 12;
    private const int MaxRecoverAttempts = 12;

    // ---- live heading state ----
    private readonly GuideCache _guide = new();
    private DateTime _titleUntilUtc = DateTime.MaxValue;

    public PlayerViewModel(LibVLC libvlc, TabloClient client)
    {
        _libvlc = libvlc;
        _client = client;
        MediaPlayer = new MediaPlayer(_libvlc) { EnableHardwareDecoding = true };
        MediaPlayer.Playing += (_, _) => OnUi(() => { IsPlaying = true; IsLoading = false; StatusMessage = ""; });
        // Windows keeps one volume-mixer entry for the app and remembers if it was muted; in 1.1.0
        // multi-view muted it (see MultiViewPane), leaving every later programme silent.
        // So once the audio output exists, set mute to what the Mute button says - which also
        // clears a mute left behind. Off the event thread, like every LibVLC call.
        MediaPlayer.Playing += (_, _) => Task.Run(() => { try { MediaPlayer.Mute = _muted; } catch { } });
        MediaPlayer.Paused += (_, _) => OnUi(() => IsPlaying = false);
        MediaPlayer.Stopped += (_, _) => OnUi(() => IsPlaying = false);
        // A weak OTA signal shows up as an error or a frozen picture — recover instead of dying.
        MediaPlayer.EncounteredError += (_, _) => TriggerRecover("playback error");
        MediaPlayer.EndReached += (_, _) => { if (IsLive) TriggerRecover("stream ended"); };

        _watchdog = new Timer(2000) { AutoReset = true };
        _watchdog.Elapsed += (_, _) => { CheckStall(); RefreshLiveTitle(); RefreshBehindLive(); };
    }

    /// <summary>
    /// Start a recovery on a thread pool thread, never on the caller's.
    ///
    /// This matters more than it looks: EncounteredError/EndReached are raised on a LibVLC
    /// event thread, and an async method runs synchronously on its caller's thread until the
    /// first await — so calling RecoverAsync directly would run MediaPlayer.Stop() *inside* the
    /// event callback. libvlc holds its event lock there while Stop() waits for the event
    /// dispatcher to drain, which deadlocks the player permanently; the next UI click that
    /// touches a MediaPlayer property then blocks the UI thread too and the window goes
    /// "Not Responding". Live TV hit this within minutes, when the stream first ended.
    /// </summary>
    private void TriggerRecover(string reason) => _ = Task.Run(() => RecoverAsync(reason));

    private volatile bool _muted;

    /// <summary>The Mute button. Kept here too, because each new stream starts its audio unmuted.</summary>
    public void SetMuted(bool muted)
    {
        _muted = muted;
        Task.Run(() => { try { MediaPlayer.Mute = muted; } catch { } });
    }

    public async Task PlayAsync(string path, string title, bool live)
    {
        BehindLive = "";
        _lastLength = 0;
        _liveGapMs = long.MaxValue;
        _pausedAtMs = -1;
        _path = path;
        Title = title;
        IsLive = live;
        _userPaused = false;
        _recoverAttempts = 0;
        // The caller resolved the heading as it tuned; ask again as soon as the watchdog runs
        // so we learn when this programme ends.
        _titleUntilUtc = DateTime.MinValue;
        await StartAsync(initial: true);
    }

    /// <summary>
    /// Follow the schedule while a channel is watched. A live tab stays open for hours, so a
    /// heading fixed at tune-in spends the evening naming a programme that has long finished.
    /// The guide cache is only re-read once what we are showing has actually ended, so this
    /// costs a comparison per tick and a file read per programme boundary — and nothing at all
    /// on a recording, whose title never changes.
    /// </summary>
    private void RefreshLiveTitle()
    {
        if (!IsLive || string.IsNullOrEmpty(_path) || DateTime.UtcNow < _titleUntilUtc) return;

        var airing = NowPlaying.HeadingFor(_guide, _client.Device?.ServerId ?? "", _path);
        // Nothing in the guide for this channel: keep the heading and look again in a while,
        // rather than re-reading the cache every two seconds for the rest of the evening.
        _titleUntilUtc = airing?.EndsUtc ?? DateTime.UtcNow.AddMinutes(5);
        if (airing is { } a) OnUi(() => Title = a.Heading);
    }

    private async Task StartAsync(bool initial)
    {
        // StartAsync also runs on the watchdog/thread-pool threads during a recovery, so every
        // bound property it touches has to go through the dispatcher.
        OnUi(() => { ErrorMessage = ""; IsLoading = true; });
        _lastTime = -1;
        _lastAdvanceUtc = DateTime.UtcNow;
        try
        {
            var watch = await _client.WatchAsync(_path);
            if (watch?.PlaylistUrl is null)
            {
                if (initial)
                    OnUi(() =>
                    {
                        IsLoading = false;
                        ErrorMessage = IsLive
                            ? "Could not tune the channel — all tuners may be in use."
                            : "Could not start the recording.";
                    });
                else TriggerRecover("no stream");
                return;
            }
            var media = new Media(_libvlc, new Uri(watch.PlaylistUrl));
            if (IsLive) media.AddOption(":network-caching=1500");
            MediaPlayer.Play(media);
            media.Dispose();
            _watchdog.Start();
        }
        catch (Exception ex)
        {
            OnUi(() => IsLoading = false);
            if (initial) OnUi(() => ErrorMessage = ex.Message);
            else TriggerRecover("start failed");
        }
    }

    /// <summary>Watchdog: if the picture is frozen (time not advancing) while not paused, re-tune.</summary>
    private void CheckStall()
    {
        if (_recoveringFlag != 0 || _userPaused || string.IsNullOrEmpty(_path)) return;
        long t;
        try { t = MediaPlayer.Time; } catch { return; }
        if (t != _lastTime && t > 0)
        {
            _lastTime = t;
            _lastAdvanceUtc = DateTime.UtcNow;
            if (_recoverAttempts != 0) _recoverAttempts = 0;   // healthy again
            return;
        }
        if ((DateTime.UtcNow - _lastAdvanceUtc).TotalSeconds >= StallSeconds)
            TriggerRecover("frozen picture");
    }

    private async Task RecoverAsync(string reason)
    {
        if (Interlocked.CompareExchange(ref _recoveringFlag, 1, 0) != 0) return;   // one at a time
        try
        {
            if (_userPaused || string.IsNullOrEmpty(_path)) return;
            if (_recoverAttempts >= MaxRecoverAttempts)
            {
                _watchdog.Stop();
                OnUi(() =>
                {
                    IsLoading = false;
                    StatusMessage = "";
                    ErrorMessage = "Lost the stream — likely a weak signal. Press Play / Pause to try again.";
                });
                return;
            }
            _recoverAttempts++;
            App.Trace($"Player recover ({reason}) attempt {_recoverAttempts}");
            var attempt = _recoverAttempts;
            OnUi(() =>
            {
                StatusMessage = $"Reconnecting…{(attempt > 1 ? $" (attempt {attempt})" : "")}";
                IsLoading = true;
            });
            // Safe here only because TriggerRecover always puts us on a thread pool thread —
            // see the note there. Stop() blocks until libvlc has torn the stream down.
            try { MediaPlayer.Stop(); } catch { /* ignore */ }
            await Task.Delay(1500);
            await StartAsync(initial: false);
        }
        finally { Interlocked.Exchange(ref _recoveringFlag, 0); }
    }

    [RelayCommand]
    public void PlayPause()
    {
        if (MediaPlayer.IsPlaying)
        {
            _userPaused = true;
            if (IsLive) _pausedAtMs = MediaPlayer.Time;
            MediaPlayer.Pause();
        }
        else
        {
            _userPaused = false;
            // If the stream died (error) or was stopped, re-tune; otherwise just resume.
            if (MediaPlayer.Media is null || !string.IsNullOrEmpty(ErrorMessage))
            {
                _recoverAttempts = 0;
                _ = StartAsync(initial: true);
            }
            else
            {
                MediaPlayer.Play();
                if (IsLive && _pausedAtMs >= 0)
                {
                    // Not straight away: a seek issued while VLC is still resuming is dropped.
                    var resumeAt = _pausedAtMs;
                    _pausedAtMs = -1;
                    Task.Run(async () =>
                    {
                        await Task.Delay(500);
                        try { if (!_userPaused && IsLive) MediaPlayer.Time = resumeAt; } catch { /* torn down */ }
                    });
                }
            }
        }
    }

    [RelayCommand]
    public void SkipBackward() => Seek(-30_000);

    [RelayCommand]
    public void SkipForward() => Seek(+30_000);

    /// <summary>
    /// Skip within what is playing. A live channel is seekable too: the Tablo's live playlist
    /// keeps every segment since the channel was tuned, and VLC reports that as a Length that
    /// grows with the broadcast. VLC will happily accept a Time past that end, though (measured:
    /// Time 646s against a Length of 87s), so live fast-forward is clamped to the live edge.
    /// </summary>
    public void Seek(long deltaMs)
    {
        if (!MediaPlayer.IsSeekable) return;
        var target = MediaPlayer.Time + deltaMs;
        if (target < 0) target = 0;
        var len = MediaPlayer.Length;
        var end = IsLive ? LiveEdgeEstimateMs() - LiveGapMs : len - 1000;
        if (len > 0 && target > end) target = Math.Max(0, end);
        MediaPlayer.Time = target;
        _pausedAtMs = -1;
        RefreshBehindLive();
    }

    /// <summary>Estimated end of what the Tablo has buffered - see <see cref="_lastLength"/>.</summary>
    private long LiveEdgeEstimateMs()
    {
        var len = MediaPlayer.Length;
        var now = DateTime.UtcNow;
        if (len != _lastLength) { _lastLength = len; _lengthChangedUtc = now; }
        return len + (long)Math.Min((now - _lengthChangedUtc).TotalMilliseconds, 60_000);
    }

    /// <summary>The gap that counts as live; a sensible default until playback has shown one.</summary>
    private long LiveGapMs => _liveGapMs == long.MaxValue ? 12_000 : _liveGapMs;

    private void RefreshBehindLive()
    {
        string text = "";
        if (IsLive)
        {
            try
            {
                var time = MediaPlayer.Time;
                if (time <= 0) return;   // mid-seek/re-tune reading; the next tick has a real one
                var gap = LiveEdgeEstimateMs() - time;
                if (MediaPlayer.State == VLCState.Playing && MediaPlayer.Length > 0 && gap < _liveGapMs)
                    _liveGapMs = gap;
                var behind = gap - LiveGapMs;
                if (MediaPlayer.Length > 0 && behind > 8000)
                {
                    var t = TimeSpan.FromMilliseconds(behind);
                    text = (t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss")) + " behind live";
                }
            }
            catch { /* player mid-teardown */ }
        }
        if (text != BehindLive) OnUi(() => BehindLive = text);
    }

    [RelayCommand]
    public void Stop()
    {
        _watchdog.Stop();
        _path = "";            // clearing this first makes any in-flight recovery bail out
        _userPaused = false;
        Title = "";
        BehindLive = "";
        ErrorMessage = "";
        StatusMessage = "";
        // Tear down off the UI thread. Stop() blocks until libvlc has drained the stream,
        // which on live TV takes long enough to freeze the window right as it's closing.
        if (MediaPlayer.Media is not null)
            Task.Run(() => { try { MediaPlayer.Stop(); } catch { /* ignore */ } });
    }

    private static void OnUi(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    public void Dispose()
    {
        _watchdog.Stop();
        _watchdog.Dispose();
        MediaPlayer.Stop();
        MediaPlayer.Dispose();
    }
}
