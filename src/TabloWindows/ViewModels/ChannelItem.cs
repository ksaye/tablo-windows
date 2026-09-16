using CommunityToolkit.Mvvm.ComponentModel;
using TabloWindows.Models;

namespace TabloWindows.ViewModels;

/// <summary>Display wrapper over a guide channel for the Live TV grid.</summary>
public sealed partial class ChannelItem(GuideChannelWrap wrap) : ObservableObject
{
    public GuideChannelWrap Wrap { get; } = wrap;
    public string Path => Wrap.Path;
    public Channel Channel => Wrap.Channel;

    public string Number => Channel.DisplayNumber;
    public string CallSign => Channel.CallSign;
    public string Network => Channel.Network ?? "";
    public string Resolution => (Channel.Resolution ?? "").StartsWith("hd") ? "HD" : "";

    /// <summary>A free streaming (FAST) channel — internet, not antenna, and not recordable.</summary>
    public bool IsFast => Services.TabloClient.IsFast(Path);

    /// <summary>The little tag beside the channel number: HD for antenna, FREE for streaming.</summary>
    public string Badge => IsFast ? "FREE" : Resolution;

    public string? LogoUrl =>
        Channel.Logos.FirstOrDefault(l => l.Kind == "lightLarge")?.Url
        ?? Channel.Logos.FirstOrDefault()?.Url;

    /// <summary>
    /// What's airing on this channel right now, resolved from the cached guide. Empty when the
    /// guide hasn't been loaded yet — the channel number is always shown, this only enriches it.
    /// Observable because the grid sits open across programme boundaries.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WatchTitle))]
    private string _nowPlaying = "";

    /// <summary>Label for the player: the programme when we know it, otherwise just the channel.</summary>
    public string WatchTitle => Services.NowPlaying.Heading(NowPlaying, Number, CallSign);
}
