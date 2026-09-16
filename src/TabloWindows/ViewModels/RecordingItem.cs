using TabloWindows.Models;
using TabloWindows.Services;

namespace TabloWindows.ViewModels;

/// <summary>Display wrapper for one row of the recordings list.</summary>
public sealed class RecordingItem(RecordingAiring airing, TabloClient client)
{
    public RecordingAiring Airing { get; } = airing;

    /// <summary>What it is doing, for anything not simply sitting there ready to play.</summary>
    public string StateLabel => IsFinished ? "" : "RECORDING";

    public bool HasStateLabel => StateLabel.Length > 0;

    public string Path => Airing.Path;

    public string Title =>
        Airing.Episode?.Title is { Length: > 0 } et ? Owner + " — " + et : Owner;

    public string Owner =>
        Airing.AiringDetails.ShowTitle
        ?? Airing.ShowTitle
        ?? Airing.Event?.Title
        ?? Airing.MovieAiring?.Title
        ?? Airing.Episode?.Title
        ?? "Recording";

    public string SubLine
    {
        get
        {
            var parts = new List<string>();
            if (Airing.Episode is { SeasonNumber: > 0, Number: > 0 } e)
                parts.Add($"S{e.SeasonNumber} E{e.Number}");
            var ch = Airing.AiringDetails.Channel?.Channel;
            if (ch is not null) parts.Add($"{ch.DisplayNumber} {ch.CallSign}");
            var when = TabloClient.ParseDate(Airing.AiringDetails.Datetime);
            if (when != DateTime.MinValue) parts.Add(when.ToLocalTime().ToString("MMM d, yyyy"));
            return string.Join("   ·   ", parts);
        }
    }

    public string Description =>
        Airing.Episode?.Description
        ?? Airing.Event?.Description
        ?? Airing.MovieAiring?.Description
        ?? "";

    public string DurationText
    {
        get
        {
            var secs = Airing.VideoDetails.Duration > 0
                ? Airing.VideoDetails.Duration
                : Airing.AiringDetails.Duration;
            var ts = TimeSpan.FromSeconds(secs);
            return ts.Hours > 0 ? $"{ts.Hours}h {ts.Minutes}m" : $"{ts.Minutes}m";
        }
    }

    public long SizeBytes => Airing.VideoDetails.Size;

    /// <summary>Still recording, or done?</summary>
    public bool IsFinished =>
        string.Equals(Airing.VideoDetails.State, "finished", StringComparison.OrdinalIgnoreCase);

    /// <summary>The device's own snapshot of the recording, when it has one.</summary>
    public string? ImageUrl =>
        Airing.SnapshotImage is { ImageId: > 0 } snap ? client.SnapshotUrl(snap.ImageId) : null;

    public DateTime StartedUtc => TabloClient.ParseDate(Airing.AiringDetails.Datetime);
}
