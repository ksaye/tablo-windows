# Changelog

## 1.1.1 — 2026-10-20

- **Fixed: playback could have no sound.** Multi-view silenced the panes you were not listening
  to with LibVLC's mute, but every player in the app shares one Windows audio session, so that
  muted the whole app in the Windows volume mixer - and Windows remembers it, so ordinary
  playback stayed silent on later launches. Panes now go quiet by switching their audio track
  off, and playback clears a mute left behind by 1.1.0.
- **Mute button** (and the M key) in the player and in multi-view, next to Full Screen and Stop.
  It holds across channel changes and sound switches, and every launch starts unmuted.

## 1.1.0 — 2026-09-17

- **Multi-view**: watch 2, 3 or 4 live channels at once, laid out side by side, two over one, or
  2×2. Each pane is its own LibVLC player on the channel's stream from the Tablo, so no server is
  needed. Sound comes from one pane, marked with a yellow border; click a pane's name or press
  ← → / 1–4 to move it. A pane whose picture freezes re-tunes itself. F11 for full screen.
  Starting multi-view closes the regular player and vice versa, since both use tuners.
- Tested on a Windows 11 VM with no GPU: four HD channels play, at 72–96% of 8 virtual CPUs.
- After leaving full screen, multi-view panes are nudged to re-lay out their picture so the
  letterbox does not keep stale pixels.

## 1.0.0 — 2026-09-16

First public release.

- Live TV (antenna and free streaming channels) with pause, rewind and fast-forward, and
  channel up/down from the keyboard.
- Recordings: play, export to `.ts`, delete.
- 14-day guide, loaded from the DVR and cached for 12 hours; watch now, record an episode or a
  series.
- Plays the DVR's MPEG-2/AC3 streams directly through a bundled LibVLC — no transcoding.
- Updates itself from GitHub releases.
