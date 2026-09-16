using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TabloWindows.Services;

namespace TabloWindows.ViewModels;

public sealed partial class LiveTvViewModel : ObservableObject
{
    private readonly TabloClient _client;
    private readonly Func<string, string, bool, Task> _play;
    private readonly GuideCache _cache = new();

    /// <summary>
    /// When the soonest programme on screen ends — the first moment any tile could be wrong.
    /// Until then there is nothing to look up, so the ticker below costs a comparison.
    /// </summary>
    private DateTime _nextBoundaryUtc = DateTime.MaxValue;
    private readonly DispatcherTimer _ticker;

    public ObservableCollection<ChannelItem> Channels { get; } = new();

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "";

    public LiveTvViewModel(TabloClient client, Func<string, string, bool, Task> play)
    {
        _client = client;
        _play = play;

        // The grid is left open for hours, so what's-on has to follow the schedule rather than
        // stay frozen at whatever was airing when the tab was last loaded. On the UI thread on
        // purpose: it writes straight into the bound items.
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _ticker.Tick += (_, _) => RefreshNowPlaying(force: false);
        _ticker.Start();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        StatusMessage = "Loading channels…";
        try
        {
            // Antenna channels and the account's free streaming ones, in one grid.
            var chans = await _client.GetAllChannelsAsync();
            Channels.Clear();
            foreach (var c in chans) Channels.Add(new ChannelItem(c));
            RefreshNowPlaying(force: true);
            StatusMessage = $"{Channels.Count} channels";
        }
        catch (Exception ex)
        {
            StatusMessage = "Failed to load channels: " + ex.Message;
        }
        finally { IsLoading = false; }
    }

    /// <summary>
    /// Fill in what's on each tile from the cached guide, and work out when the first of them
    /// ends. Skipped entirely until that moment arrives, so a quiet hour costs three ticks a
    /// minute and no file reads.
    /// </summary>
    private void RefreshNowPlaying(bool force)
    {
        // Nothing on screen yet — before sign-in, or the tab has never been opened.
        if (Channels.Count == 0) return;
        if (!force && DateTime.UtcNow < _nextBoundaryUtc) return;

        var now = NowPlaying.ByChannel(_cache, _client.Device?.ServerId ?? "");
        var next = DateTime.MaxValue;
        foreach (var item in Channels)
        {
            if (!now.TryGetValue(item.Path, out var airing)) continue;
            item.NowPlaying = airing.Title;
            if (airing.EndsUtc < next) next = airing.EndsUtc;
        }

        // With no guide at all there is no boundary to wait for; try again in a few minutes
        // rather than re-reading the cache on every tick for the rest of the evening.
        _nextBoundaryUtc = next == DateTime.MaxValue ? DateTime.UtcNow.AddMinutes(5) : next;
    }

    [RelayCommand]
    private async Task Watch(ChannelItem? item)
    {
        if (item is null) return;
        // The tiles follow the schedule now, and the player re-resolves its heading as soon as
        // it starts, so what is on the tile is what we tuned to.
        await _play(item.Path, item.WatchTitle, true);
    }
}
