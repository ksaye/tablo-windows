using System.Text.Json.Serialization;

namespace TabloWindows.Models;

// ---- Cloud auth (lighthousetv.ewscloud.com) ----

public sealed class LoginResponse
{
    [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("is_verified")] public bool IsVerified { get; set; }
    [JsonPropertyName("code")] public int? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class AccountResponse
{
    [JsonPropertyName("identifier")] public string? Identifier { get; set; }
    [JsonPropertyName("profiles")] public List<Profile> Profiles { get; set; } = new();
    [JsonPropertyName("devices")] public List<TabloDevice> Devices { get; set; } = new();
}

public sealed class Profile
{
    [JsonPropertyName("identifier")] public string Identifier { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class TabloDevice
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("serverId")] public string ServerId { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";

    /// <summary>Host/IP part of the device URL, used to tell same-named devices apart.</summary>
    [JsonIgnore]
    public string Host { get { try { return new Uri(Url).Host; } catch { return Url; } } }

    /// <summary>Dropdown label — devices are all named "Tablo", so show the IP too.</summary>
    [JsonIgnore]
    public string Display => $"{Name} · {Host}";

    public override string ToString() => $"{Name} ({ServerId})";
}

public sealed class SelectResponse
{
    [JsonPropertyName("token")] public string? Token { get; set; }
}

// ---- Device: /server/info ----

public sealed class ServerInfo
{
    [JsonPropertyName("server_id")] public string ServerId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("availability")] public string Availability { get; set; } = "";
    [JsonPropertyName("model")] public ServerModel Model { get; set; } = new();
}

public sealed class ServerModel
{
    [JsonPropertyName("wifi")] public bool Wifi { get; set; }
    [JsonPropertyName("tuners")] public int Tuners { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

// ---- Device: /server/harddrives ----

/// <summary>
/// Recording storage totals. <paramref name="Raw"/> keeps the untouched response so a
/// firmware whose shape we didn't anticipate can still be inspected/logged.
/// </summary>
public sealed record StorageInfo(long TotalBytes, long FreeBytes, System.Text.Json.JsonElement Raw)
{
    /// <summary>
    /// Firmware has been seen to report MB rather than bytes. No real Tablo has under a
    /// gigabyte of recording space, so a suspiciously small total means the units are MB.
    /// </summary>
    public StorageInfo Normalized() =>
        TotalBytes is > 0 and < 1_000_000_000
            ? this with { TotalBytes = TotalBytes * 1_048_576L, FreeBytes = FreeBytes * 1_048_576L }
            : this;

    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
}

// ---- Guide channels ----

public sealed class GuideChannelWrap
{
    [JsonPropertyName("object_id")] public long ObjectId { get; set; }
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("channel")] public Channel Channel { get; set; } = new();
}

public sealed class Channel
{
    [JsonPropertyName("call_sign")] public string CallSign { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("major")] public int Major { get; set; }
    [JsonPropertyName("minor")] public int Minor { get; set; }
    [JsonPropertyName("network")] public string? Network { get; set; }
    [JsonPropertyName("resolution")] public string? Resolution { get; set; }
    [JsonPropertyName("logos")] public List<ChannelLogo> Logos { get; set; } = new();

    [JsonIgnore]
    public string DisplayNumber => Minor > 0 ? $"{Major}.{Minor}" : Major.ToString();
}

public sealed class ChannelLogo
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

// ---- Cloud channel lineup (OTA + FAST) ----

/// <summary>
/// One channel from the account's cloud lineup
/// (<c>/api/v2/account/{lighthouse}/guide/channels/</c>). This is the only place the free
/// ad-supported streaming ("FAST") channels appear — the device on :8887 lists antenna
/// channels and nothing else.
/// </summary>
public sealed class LineupChannel
{
    [JsonPropertyName("identifier")] public string Identifier { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>"ota" for an antenna channel, "ott" for a FAST streaming channel.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("logos")] public List<ChannelLogo> Logos { get; set; } = new();
    [JsonPropertyName("ota")] public LineupFeed? Ota { get; set; }
    [JsonPropertyName("ott")] public LineupFeed? Ott { get; set; }
}

public sealed class LineupFeed
{
    [JsonPropertyName("major")] public int Major { get; set; }
    [JsonPropertyName("minor")] public int Minor { get; set; }
    [JsonPropertyName("callSign")] public string? CallSign { get; set; }
    [JsonPropertyName("network")] public string? Network { get; set; }
    /// <summary>FAST only: the HLS playlist, straight from the channel partner's CDN.</summary>
    [JsonPropertyName("streamUrl")] public string? StreamUrl { get; set; }
    /// <summary>
    /// What the cloud says about recording this channel. Note that even a "true" here is not
    /// reachable through this API: the DVR only records what its own tuners can see.
    /// </summary>
    [JsonPropertyName("canRecord")] public bool CanRecord { get; set; }
}

/// <summary>One programme from the cloud guide for a single channel and day.</summary>
public sealed class CloudAiring
{
    [JsonPropertyName("identifier")] public string Identifier { get; set; } = "";
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("datetime")] public string? Datetime { get; set; }
    [JsonPropertyName("duration")] public int Duration { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    /// <summary>"episode" or "movieAiring".</summary>
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("genres")] public List<string> Genres { get; set; } = new();
    [JsonPropertyName("show")] public CloudShow? Show { get; set; }
    [JsonPropertyName("episode")] public CloudEpisode? Episode { get; set; }
    [JsonPropertyName("movieAiring")] public CloudMovie? MovieAiring { get; set; }
}

public sealed class CloudShow
{
    [JsonPropertyName("title")] public string? Title { get; set; }
}

public sealed class CloudEpisode
{
    [JsonPropertyName("season")] public CloudSeason? Season { get; set; }
    [JsonPropertyName("episodeNumber")] public int? EpisodeNumber { get; set; }
    [JsonPropertyName("originalAirDate")] public string? OriginalAirDate { get; set; }
}

public sealed class CloudSeason
{
    [JsonPropertyName("number")] public int? Number { get; set; }
}

public sealed class CloudMovie
{
    [JsonPropertyName("releaseYear")] public int? ReleaseYear { get; set; }
}

// ---- Recordings (airings) ----

public sealed class RecordingAiring
{
    [JsonPropertyName("object_id")] public long ObjectId { get; set; }
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("series_path")] public string? SeriesPath { get; set; }
    [JsonPropertyName("sport_path")] public string? SportPath { get; set; }
    [JsonPropertyName("snapshot_image")] public SnapshotImage? SnapshotImage { get; set; }
    [JsonPropertyName("airing_details")] public AiringDetails AiringDetails { get; set; } = new();
    [JsonPropertyName("video_details")] public VideoDetails VideoDetails { get; set; } = new();
    [JsonPropertyName("user_info")] public UserInfo? UserInfo { get; set; }
    [JsonPropertyName("episode")] public EpisodeInfo? Episode { get; set; }
    [JsonPropertyName("event")] public EventInfo? Event { get; set; }
    [JsonPropertyName("movie_airing")] public MovieInfo? MovieAiring { get; set; }
    [JsonPropertyName("show_title")] public string? ShowTitle { get; set; }
}

public sealed class SnapshotImage
{
    [JsonPropertyName("image_id")] public long ImageId { get; set; }
    [JsonPropertyName("has_title")] public bool HasTitle { get; set; }
}

public sealed class AiringDetails
{
    [JsonPropertyName("datetime")] public string? Datetime { get; set; }
    [JsonPropertyName("duration")] public int Duration { get; set; }
    [JsonPropertyName("channel_path")] public string? ChannelPath { get; set; }
    [JsonPropertyName("channel")] public RecChannelWrap? Channel { get; set; }
    [JsonPropertyName("show_title")] public string? ShowTitle { get; set; }
}

public sealed class RecChannelWrap
{
    [JsonPropertyName("channel")] public Channel? Channel { get; set; }
}

public sealed class VideoDetails
{
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("clean")] public bool Clean { get; set; }
    [JsonPropertyName("audio")] public string? Audio { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("duration")] public int Duration { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
}

public sealed class UserInfo
{
    [JsonPropertyName("position")] public int Position { get; set; }
    [JsonPropertyName("watched")] public bool Watched { get; set; }
    [JsonPropertyName("protected")] public bool Protected { get; set; }
}

public sealed class EpisodeInfo
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("number")] public int? Number { get; set; }
    [JsonPropertyName("season_number")] public int? SeasonNumber { get; set; }
    [JsonPropertyName("orig_air_date")] public string? OrigAirDate { get; set; }
}

public sealed class EventInfo
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("venue")] public string? Venue { get; set; }
}

public sealed class MovieInfo
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("release_year")] public int? ReleaseYear { get; set; }
}

// ---- Recordings shows (series/sports/movies containers) ----

public sealed class RecordingShow
{
    [JsonPropertyName("object_id")] public long ObjectId { get; set; }
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("series")] public SeriesInfo? Series { get; set; }
    [JsonPropertyName("sport")] public SportInfo? Sport { get; set; }
    [JsonPropertyName("movie")] public MovieInfo? Movie { get; set; }
}

public sealed class SeriesInfo
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
}

public sealed class SportInfo
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
}

// ---- Guide airings (schedulable) ----

public sealed class GuideAiring
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("object_id")] public long ObjectId { get; set; }
    [JsonPropertyName("series_path")] public string? SeriesPath { get; set; }
    [JsonPropertyName("sport_path")] public string? SportPath { get; set; }
    [JsonPropertyName("airing_details")] public AiringDetails AiringDetails { get; set; } = new();
    [JsonPropertyName("episode")] public EpisodeInfo? Episode { get; set; }
    [JsonPropertyName("event")] public EventInfo? Event { get; set; }
    [JsonPropertyName("movie_airing")] public MovieInfo? MovieAiring { get; set; }
    [JsonPropertyName("qualifiers")] public List<string> Qualifiers { get; set; } = new();
    [JsonPropertyName("schedule")] public ScheduleInfo Schedule { get; set; } = new();

    [JsonIgnore]
    public string Title =>
        AiringDetails.ShowTitle
        ?? Episode?.Title
        ?? Event?.Title
        ?? MovieAiring?.Title
        ?? "Program";

    [JsonIgnore]
    public string Description =>
        Episode?.Description ?? Event?.Description ?? MovieAiring?.Description ?? "";
}

public sealed class ScheduleInfo
{
    [JsonPropertyName("state")] public string State { get; set; } = "none";
    [JsonPropertyName("qualifier")] public string? Qualifier { get; set; }
    [JsonPropertyName("skip_reason")] public string? SkipReason { get; set; }
    [JsonPropertyName("rule")] public string? Rule { get; set; }   // present on series objects
}

public sealed class GuideSeries
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("schedule")] public ScheduleInfo Schedule { get; set; } = new();
    [JsonPropertyName("schedule_rule")] public string? ScheduleRule { get; set; }
    [JsonPropertyName("series")] public SeriesInfo? Series { get; set; }
}

// ---- Playback (/watch) ----

public sealed class WatchResponse
{
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("expires")] public string? Expires { get; set; }
    [JsonPropertyName("keepalive")] public int Keepalive { get; set; }
    [JsonPropertyName("playlist_url")] public string? PlaylistUrl { get; set; }
    [JsonPropertyName("audio_details")] public AudioDetails? AudioDetails { get; set; }
}

public sealed class AudioDetails
{
    [JsonPropertyName("container_format")] public string? ContainerFormat { get; set; }
}
