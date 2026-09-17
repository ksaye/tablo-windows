using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using TabloWindows.Services;
using Timer = System.Timers.Timer;

namespace TabloWindows.ViewModels;

/// <summary>A channel on the multi-view picker, with its place in the selection (1-4, 0 = not picked).</summary>
public sealed partial class MultiViewChoice(ChannelItem channel) : ObservableObject
{
    public ChannelItem Channel { get; } = channel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelected))]
    private int _order;

    public bool IsSelected => Order > 0;
}

/// <summary>
/// One tile of a running multi-view: its own LibVLC player on its own live stream from the Tablo.
/// Each pane holds a tuner (a free streaming channel does not) for as long as it plays.
/// </summary>
public sealed partial class MultiViewPane : ObservableObject
{
    public MultiViewPane(LibVLC libvlc, int index)
    {
        Index = index;
        Player = new MediaPlayer(libvlc) { EnableHardwareDecoding = true, Mute = true };
        // VLC resets mute when a stream's audio output starts, so apply it again once playing.
        // Off the event thread: LibVLC holds its event lock while raising this.
        Player.Playing += (_, _) => Task.Run(() => Player.Mute = !HasSound);
    }

    public int Index { get; }
    public int Number => Index + 1;
    public MediaPlayer Player { get; }

    /// <summary>The channel path playing here, or empty when this pane is not in use.</summary>
    public string Path { get; set; } = "";

    [ObservableProperty] private bool _inUse;
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _hasSound;

    // Stall detection: the Tablo can drop one channel (a weak signal) while the others carry on.
    internal long LastTime = -1;
    internal DateTime LastAdvanceUtc;
    internal int Recovering;
}

/// <summary>
/// Multi-view: 2-4 live channels at once, each decoded by its own player, with the sound from one
/// of them. A PC has the decoding power for four broadcast streams, so unlike a browser or a TV
/// stick it needs no server to combine them into one picture.
/// </summary>
public sealed partial class MultiViewViewModel : ObservableObject, IDisposable
{
    public const int MaxPanes = 4;
    private const int StallSeconds = 15;

    private readonly LibVLC _libvlc;
    private readonly TabloClient _client;
    private readonly LiveTvViewModel _liveTv;
    private readonly Timer _watchdog = new(3000) { AutoReset = true };

    public ObservableCollection<MultiViewChoice> Choices { get; } = new();
    public IReadOnlyList<MultiViewPane> Panes { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private int _selectedCount;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private int _paneCount;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "";

    public MultiViewViewModel(LibVLC libvlc, TabloClient client, LiveTvViewModel liveTv)
    {
        _libvlc = libvlc;
        _client = client;
        _liveTv = liveTv;
        Panes = Enumerable.Range(0, MaxPanes).Select(i => new MultiViewPane(libvlc, i)).ToList();
        _watchdog.Elapsed += (_, _) => CheckStalls();
    }

    /// <summary>Fill the picker from the Live TV channel list, loading it first if need be.</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            if (_liveTv.Channels.Count == 0) await _liveTv.LoadAsync();
            var picked = Choices.Where(c => c.IsSelected).ToDictionary(c => c.Channel.Path, c => c.Order);
            Choices.Clear();
            foreach (var channel in _liveTv.Channels)
                Choices.Add(new MultiViewChoice(channel) { Order = picked.GetValueOrDefault(channel.Path) });
            SelectedCount = Choices.Count(c => c.IsSelected);
            StatusMessage = Choices.Count == 0 ? _liveTv.StatusMessage : "";
        }
        finally { IsLoading = false; }
    }

    /// <summary>Pick or un-pick a channel. Picks are numbered in the order made; that is the pane order.</summary>
    [RelayCommand]
    private void Toggle(MultiViewChoice? choice)
    {
        if (choice is null) return;
        if (choice.IsSelected)
        {
            var removed = choice.Order;
            choice.Order = 0;
            foreach (var c in Choices.Where(c => c.Order > removed)) c.Order--;
        }
        else
        {
            if (SelectedCount >= MaxPanes) return;
            choice.Order = SelectedCount + 1;
        }
        SelectedCount = Choices.Count(c => c.IsSelected);
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var c in Choices) c.Order = 0;
        SelectedCount = 0;
    }

    private bool CanStart => SelectedCount >= 2;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var picks = Choices.Where(c => c.IsSelected).OrderBy(c => c.Order).Take(MaxPanes).ToList();
        if (picks.Count < 2) return;

        PaneCount = picks.Count;
        IsOpen = true;
        for (var i = 0; i < MaxPanes; i++)
        {
            var pane = Panes[i];
            pane.InUse = i < picks.Count;
            pane.HasSound = i == 0;
            pane.Player.Mute = i != 0;
            if (!pane.InUse) { pane.Path = ""; continue; }
            pane.Path = picks[i].Channel.Path;
            pane.Label = picks[i].Channel.WatchTitle;
            pane.Status = "Tuning…";
        }

        // One at a time: several tune requests at the same instant are more than the Tablo likes.
        foreach (var pane in Panes.Where(p => p.InUse))
        {
            if (!IsOpen) return;
            await TuneAsync(pane);
        }
        _watchdog.Start();
    }

    private async Task TuneAsync(MultiViewPane pane)
    {
        var path = pane.Path;
        try
        {
            var watch = await Task.Run(() => _client.WatchAsync(path));
            if (!IsOpen || pane.Path != path) return;
            if (watch?.PlaylistUrl is null)
            {
                OnUi(() => pane.Status = "Could not tune — every tuner may be in use.");
                return;
            }
            var media = new Media(_libvlc, new Uri(watch.PlaylistUrl));
            media.AddOption(":network-caching=1500");
            pane.LastTime = -1;
            pane.LastAdvanceUtc = DateTime.UtcNow;
            await Task.Run(() => pane.Player.Play(media));
            media.Dispose();
            OnUi(() => pane.Status = "");
        }
        catch (Exception ex)
        {
            OnUi(() => pane.Status = "Could not tune: " + ex.Message);
        }
    }

    /// <summary>Move the sound to pane <paramref name="index"/> (0-based).</summary>
    public void SetSound(int index)
    {
        if (!IsOpen || index < 0 || index >= PaneCount) return;
        foreach (var pane in Panes)
        {
            pane.HasSound = pane.Index == index;
            pane.Player.Mute = pane.Index != index;
        }
    }

    [RelayCommand]
    private void SelectSound(MultiViewPane? pane)
    {
        if (pane is not null) SetSound(pane.Index);
    }

    /// <summary>Step the sound to the next (+1) or previous (-1) pane, wrapping.</summary>
    public void StepSound(int delta)
    {
        if (!IsOpen || PaneCount == 0) return;
        var current = Panes.FirstOrDefault(p => p.HasSound)?.Index ?? 0;
        SetSound(((current + delta) % PaneCount + PaneCount) % PaneCount);
    }

    /// <summary>
    /// A pane whose picture has stopped moving is re-tuned on its own. Runs on a timer thread;
    /// LibVLC calls here are fine, but never from inside a LibVLC event callback.
    /// </summary>
    private void CheckStalls()
    {
        if (!IsOpen) return;
        foreach (var pane in Panes.Where(p => p.InUse && p.Path.Length > 0))
        {
            long time;
            try { time = pane.Player.Time; } catch { continue; }
            if (time > 0 && time != pane.LastTime)
            {
                pane.LastTime = time;
                pane.LastAdvanceUtc = DateTime.UtcNow;
                continue;
            }
            if ((DateTime.UtcNow - pane.LastAdvanceUtc).TotalSeconds < StallSeconds) continue;
            if (Interlocked.CompareExchange(ref pane.Recovering, 1, 0) != 0) continue;

            _ = Task.Run(async () =>
            {
                try
                {
                    OnUi(() => pane.Status = "Reconnecting…");
                    try { pane.Player.Stop(); } catch { /* ignore */ }
                    await Task.Delay(1500);
                    if (IsOpen) await TuneAsync(pane);
                }
                finally { Interlocked.Exchange(ref pane.Recovering, 0); }
            });
        }
    }

    [RelayCommand]
    public void Stop()
    {
        if (!IsOpen) return;
        _watchdog.Stop();
        IsOpen = false;
        foreach (var pane in Panes)
        {
            pane.Path = "";
            pane.InUse = false;
            pane.Status = "";
        }
        // Stop() blocks until libvlc has drained the stream; four of them would freeze the window.
        Task.Run(() =>
        {
            foreach (var pane in Panes)
                try { pane.Player.Stop(); } catch { /* ignore */ }
        });
    }

    private static void OnUi(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    public void Dispose()
    {
        _watchdog.Stop();
        _watchdog.Dispose();
        foreach (var pane in Panes)
        {
            try { pane.Player.Stop(); } catch { /* ignore */ }
            pane.Player.Dispose();
        }
    }
}
