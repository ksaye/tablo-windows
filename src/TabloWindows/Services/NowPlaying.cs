using TabloWindows.Models;

namespace TabloWindows.Services;

/// <summary>A programme showing right now, and the moment it stops.</summary>
public sealed record NowAiring(string Title, DateTime EndsUtc);

/// <summary>
/// Resolves the current programme for a channel from the on-disk guide cache.
///
/// Cache-only on purpose: resolving this from the device would mean a full ~19k-airing guide
/// load, and hammering the box is exactly what makes live playback unstable. If the guide has
/// never been loaded the channel number still shows, just without a programme name.
///
/// Every caller is showing something that expires, so each lookup hands back the end of the
/// airing too — that is what lets them wait for a boundary instead of polling.
/// </summary>
public static class NowPlaying
{
    /// <summary>How stale a cached guide may be and still describe what is on now.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    /// <summary>Current programme on every channel the cached guide covers, keyed by channel path.</summary>
    public static Dictionary<string, NowAiring> ByChannel(GuideCache cache, string deviceId)
    {
        var current = new Dictionary<string, NowAiring>(StringComparer.Ordinal);
        if (cache.Load(deviceId, MaxAge) is not { } c) return current;

        var now = DateTime.UtcNow;
        foreach (var a in c.airings)
        {
            if (a.AiringDetails.ChannelPath is not { } path) continue;
            if (Airs(a.AiringDetails, now) is not { } ends) continue;
            current[path] = new NowAiring(a.Title, ends);
        }
        return current;
    }

    /// <summary>
    /// The heading the player shows for a live channel, and when it expires. Null when the guide
    /// has nothing for this channel — the caller keeps whatever it is already showing.
    /// </summary>
    public static (string Heading, DateTime EndsUtc)? HeadingFor(
        GuideCache cache, string deviceId, string channelPath)
    {
        if (cache.Load(deviceId, MaxAge) is not { } c) return null;

        var now = DateTime.UtcNow;
        foreach (var a in c.airings)
        {
            if (a.AiringDetails.ChannelPath != channelPath) continue;
            if (Airs(a.AiringDetails, now) is not { } ends) continue;

            var channel = c.channels.FirstOrDefault(ch => ch.Path == channelPath)?.Channel;
            return (channel is null
                ? a.Title
                : Heading(a.Title, channel.DisplayNumber, channel.CallSign), ends);
        }
        return null;
    }

    /// <summary>Player heading: the programme when we know it, otherwise just the channel.</summary>
    public static string Heading(string? programme, string number, string callSign) =>
        string.IsNullOrWhiteSpace(programme)
            ? $"{number} {callSign}"
            : $"{programme} · {number} {callSign}";

    /// <summary>End of the airing if it covers <paramref name="now"/>, else null.</summary>
    private static DateTime? Airs(AiringDetails details, DateTime now)
    {
        var starts = TabloClient.ParseDate(details.Datetime);
        var ends = starts.AddSeconds(details.Duration);
        return starts <= now && ends > now ? ends : null;
    }
}
