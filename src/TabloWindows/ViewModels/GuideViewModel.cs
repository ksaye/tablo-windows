using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TabloWindows.Models;
using TabloWindows.Services;

namespace TabloWindows.ViewModels;

public sealed partial class GuideViewModel : ObservableObject
{
    public const double PxPerMin = 5.0;
    private const int RowMinutes = 24 * 60;

    private readonly TabloClient _client;
    private readonly Func<string, string, bool, Task> _play;
    private readonly GuideCache _cache = new();

    private List<GuideAiring> _all = new();
    private List<GuideChannelWrap> _channels = new();
    private bool _loaded;

    public ObservableCollection<GuideChannelRow> Rows { get; } = new();
    public ObservableCollection<DayOption> Days { get; } = new();

    [ObservableProperty] private DayOption? _selectedDay;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private double _loadProgress;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private GuideBlock? _selectedBlock;

    public double DayWidthPx => RowMinutes * PxPerMin;
    public IReadOnlyList<TimeTick> TimeTicks { get; }

    /// <summary>Raised after a rebuild so the view can scroll the grid to a useful position (now).</summary>
    public event Action<double>? ScrollRequested;

    public GuideViewModel(TabloClient client, Func<string, string, bool, Task> play)
    {
        _client = client;
        _play = play;

        var ticks = new List<TimeTick>();
        for (int m = 0; m < RowMinutes; m += 30)
            ticks.Add(new TimeTick(m * PxPerMin, DateTime.Today.AddMinutes(m).ToString("h:mm tt")));
        TimeTicks = ticks;

        var start = DateTime.Today;
        for (int i = 0; i < 14; i++)
        {
            var d = start.AddDays(i);
            Days.Add(new DayOption(d, i == 0 ? "Today" : i == 1 ? "Tomorrow" : d.ToString("ddd, MMM d")));
        }
        _selectedDay = Days[0];
    }

    partial void OnSelectedDayChanged(DayOption? value)
    {
        if (_loaded) RebuildGrid();
    }

    /// <summary>Load from the local cache if fresh, else from the device. Called when opening the tab.</summary>
    public Task LoadAsync() => LoadInternalAsync(force: false);

    /// <summary>Force a fresh load from the device (the Refresh button), ignoring the cache.</summary>
    [RelayCommand]
    private Task Refresh() => LoadInternalAsync(force: true);

    /// <summary>
    /// Fall back to the last guide we saved, at any age. A guide a day old is far more useful
    /// than an empty grid, as long as it says so.
    /// </summary>
    private bool ShowCached(string deviceId, string why)
    {
        var cached = _cache.Load(deviceId, TimeSpan.FromDays(14));
        if (cached is not { } c) return false;

        _channels = c.channels;
        _all = c.airings;
        _loaded = true;
        RebuildGrid();
        StatusMessage = $"{_channels.Count} channels · {_all.Count:N0} airings · " +
                        $"saved {c.savedAt.ToLocalTime():MMM d h:mm tt} — {why}";
        return true;
    }

    private async Task LoadInternalAsync(bool force)
    {
        if (IsLoading) return;
        IsLoading = true;
        LoadProgress = 0;
        try
        {
            var deviceId = _client.Device?.ServerId ?? "";

            // Cache-first: instant, and doesn't touch the device (which may be busy exporting).
            if (!force)
            {
                var cached = _cache.Load(deviceId, TimeSpan.FromHours(12));
                if (cached is { } c)
                {
                    _channels = c.channels;
                    _all = c.airings;
                    _loaded = true;
                    RebuildGrid();
                    StatusMessage =
                        $"{_channels.Count} channels · {_all.Count:N0} airings · cached {c.savedAt.ToLocalTime():MMM d h:mm tt} — Refresh to update";
                    return;
                }
            }

            StatusMessage = "Loading guide…";

            // Loading the guide is hundreds of batch calls - the hardest thing anything asks of the
            // DVR - so the result is cached and reused for 12 hours unless Refresh is pressed.
            if (_channels.Count == 0 || force)
            {
                App.Trace("Guide: loading channels");
                _channels = await _client.GetAllChannelsAsync();
                App.Trace($"Guide: {_channels.Count} channels loaded");
            }

            // The device's own listings are the long pole; the streaming channels' listings
            // come from the cloud afterwards. Split the bar so neither stage looks stuck.
            var progress = new Progress<double>(p => LoadProgress = p * 0.8);
            App.Trace("Guide: loading airings");
            _all = await _client.GetGuideAiringsAsync(progress);
            App.Trace($"Guide: {_all.Count} airings loaded");

            var fastChannels = _channels.Where(c => TabloClient.IsFast(c.Path)).ToList();
            if (fastChannels.Count > 0)
            {
                StatusMessage = "Loading free streaming listings…";
                try
                {
                    var fastProgress = new Progress<double>(p => LoadProgress = 0.8 + p * 0.2);
                    var fast = await _client.GetFastGuideAiringsAsync(fastChannels, progress: fastProgress);
                    App.Trace($"Guide: {fast.Count} free streaming airings loaded");
                    _all.AddRange(fast);
                }
                catch (Exception ex)
                {
                    // The antenna guide is loaded and usable; say so rather than failing it all.
                    App.Trace("Guide: free streaming listings failed: " + ex);
                }
            }

            _loaded = true;
            RebuildGrid();
            _cache.Save(deviceId, _channels, _all);
            StatusMessage = $"{_channels.Count} channels · {_all.Count:N0} airings";
        }
        catch (Exception ex)
        {
            App.Trace("Guide load failed: " + ex);
            if (!ShowCached(_client.Device?.ServerId ?? "", ex.Message))
                StatusMessage = "Failed to load guide: " + ex.Message;
        }
        finally { IsLoading = false; }
    }

    private void RebuildGrid()
    {
        if (SelectedDay is null) return;
        var dayStart = SelectedDay.Date;               // local midnight
        var dayEnd = dayStart.AddDays(1);

        var byChannel = _all
            .Where(a => a.AiringDetails.ChannelPath is not null)
            .GroupBy(a => a.AiringDetails.ChannelPath!)
            .ToDictionary(g => g.Key, g => g.ToList());

        Rows.Clear();
        foreach (var ch in _channels)
        {
            var row = new GuideChannelRow(
                ch.Channel.DisplayNumber, ch.Channel.CallSign, TabloClient.IsFast(ch.Path));
            if (byChannel.TryGetValue(ch.Path, out var airings))
            {
                foreach (var a in airings.OrderBy(a => TabloClient.ParseDate(a.AiringDetails.Datetime)))
                {
                    var startUtc = TabloClient.ParseDate(a.AiringDetails.Datetime);
                    if (startUtc == DateTime.MinValue) continue;
                    var startLocal = startUtc.ToLocalTime();
                    var endLocal = startLocal.AddSeconds(a.AiringDetails.Duration);
                    if (endLocal <= dayStart || startLocal >= dayEnd) continue;  // not this day

                    var clippedStart = startLocal < dayStart ? dayStart : startLocal;
                    var clippedEnd = endLocal > dayEnd ? dayEnd : endLocal;
                    var left = (clippedStart - dayStart).TotalMinutes * PxPerMin;
                    var width = (clippedEnd - clippedStart).TotalMinutes * PxPerMin;
                    if (width < 6) continue;  // too small to render/click

                    row.Blocks.Add(new GuideBlock(a, left, width, startLocal));
                }
            }
            Rows.Add(row);
        }

        // Scroll to "now" (with a little lead-in) for today; otherwise start at the day's beginning.
        var now = DateTime.Now;
        var offset = (now >= dayStart && now < dayEnd)
            ? Math.Max(0, (now - dayStart).TotalMinutes * PxPerMin - 40)
            : 0;
        ScrollRequested?.Invoke(offset);
    }

    [RelayCommand]
    private void SelectBlock(GuideBlock? block) => SelectedBlock = block;

    [RelayCommand]
    private async Task RecordEpisode(GuideBlock? block)
    {
        block ??= SelectedBlock;
        if (block is null) return;
        var newState = block.IsScheduled ? false : true;
        try
        {
            await _client.ScheduleAiringAsync(block.Airing.Path, newState);
            block.Airing.Schedule.State = newState ? "scheduled" : "none";
            block.RefreshState();
        }
        catch (Exception ex) { MessageBox.Show("Scheduling failed: " + ex.Message); }
    }

    [RelayCommand]
    private async Task RecordSeries(string? rule)
    {
        var block = SelectedBlock;
        if (block?.Airing.SeriesPath is not { } sp) return;
        try
        {
            await _client.SetSeriesRuleAsync(sp, rule ?? "all");
            StatusMessage = rule == "none"
                ? "Series recording turned off."
                : $"Recording {(rule == "new" ? "new" : "all")} episodes of “{block.Title}”.";
        }
        catch (Exception ex) { MessageBox.Show("Series scheduling failed: " + ex.Message); }
    }

    [RelayCommand]
    private async Task WatchNow()
    {
        var block = SelectedBlock;
        if (block?.ChannelPath is not { } channelPath) return;
        // It's airing now — tune the channel live.
        await _play(channelPath, block.Title, true);
    }
}

public sealed record DayOption(DateTime Date, string Label);
public sealed record TimeTick(double Left, string Label);

public sealed class GuideChannelRow(string number, string callSign, bool isFast = false)
{
    public string Number { get; } = number;
    public string CallSign { get; } = callSign;
    /// <summary>A free streaming channel rather than one of the antenna channels.</summary>
    public bool IsFast { get; } = isFast;
    public ObservableCollection<GuideBlock> Blocks { get; } = new();
}

public sealed partial class GuideBlock : ObservableObject
{
    public GuideAiring Airing { get; }
    public double Left { get; }
    public double Width { get; }
    public string Title => Airing.Title;
    public string TimeText { get; }

    public DateTime StartLocal { get; }
    public DateTime EndLocal { get; }
    /// <summary>True while the program is currently airing — enables "Watch now".</summary>
    public bool IsOnNow { get { var n = DateTime.Now; return n >= StartLocal && n < EndLocal; } }
    public string? ChannelPath => Airing.AiringDetails.ChannelPath;

    [ObservableProperty] private Brush _fillBrush = Brushes.Transparent;
    [ObservableProperty] private Brush _strokeBrush = Brushes.Transparent;

    public bool IsScheduled => Airing.Schedule.State is "scheduled";
    public bool IsConflict => Airing.Schedule.State is "conflict";

    /// <summary>
    /// False for a free streaming programme: those arrive over the internet, so the DVR's
    /// tuners never see them and there is nothing it can record.
    /// </summary>
    public bool CanRecord => !TabloClient.IsFast(Airing.Path);

    public GuideBlock(GuideAiring airing, double left, double width, DateTime startLocal)
    {
        Airing = airing;
        Left = left;
        Width = width;
        StartLocal = startLocal;
        EndLocal = startLocal.AddSeconds(airing.AiringDetails.Duration);
        TimeText = startLocal.ToString("h:mm tt");
        RefreshState();
    }

    public void RefreshState()
    {
        (FillBrush, StrokeBrush) = Airing.Schedule.State switch
        {
            "scheduled" => (Brush(0x1E, 0x3A, 0x2E), Brush(0x3F, 0xC9, 0x8A)),
            "conflict" => (Brush(0x3A, 0x22, 0x22), Brush(0xFF, 0x6B, 0x6B)),
            _ => (Brush(0x1E, 0x25, 0x2D), Brush(0x2A, 0x32, 0x3C)),
        };
        OnPropertyChanged(nameof(IsScheduled));
        OnPropertyChanged(nameof(IsConflict));
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}
