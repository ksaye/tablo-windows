using System.IO;
using System.Text.Json;
using TabloWindows.Models;

namespace TabloWindows.Services;

/// <summary>
/// Persists the loaded guide to disk so re-opening it (even after a restart) is instant and
/// doesn't hit the device — important when the little Tablo is busy (e.g. exporting).
/// </summary>
public sealed class GuideCache
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabloWindows");
    private static readonly string File_ = Path.Combine(Dir, "guide-cache.json");

    private sealed class Payload
    {
        public DateTime SavedAt { get; set; }
        public string DeviceId { get; set; } = "";
        public List<GuideChannelWrap> Channels { get; set; } = new();
        public List<GuideAiring> Airings { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Return the cached guide if it's for this device and newer than maxAge; else null.</summary>
    public (List<GuideChannelWrap> channels, List<GuideAiring> airings, DateTime savedAt)? Load(
        string deviceId, TimeSpan maxAge)
    {
        try
        {
            if (!File.Exists(File_)) return null;
            var p = JsonSerializer.Deserialize<Payload>(File.ReadAllText(File_), Json);
            if (p is null || p.DeviceId != deviceId || p.Airings.Count == 0) return null;
            if (DateTime.UtcNow - p.SavedAt > maxAge) return null;
            return (p.Channels, p.Airings, p.SavedAt);
        }
        catch { return null; }
    }

    public void Save(string deviceId, List<GuideChannelWrap> channels, List<GuideAiring> airings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var p = new Payload
            {
                SavedAt = DateTime.UtcNow, DeviceId = deviceId, Channels = channels, Airings = airings
            };
            File.WriteAllText(File_, JsonSerializer.Serialize(p));
        }
        catch { /* cache is best-effort */ }
    }
}
