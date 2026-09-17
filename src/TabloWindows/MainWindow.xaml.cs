using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TabloWindows.Services;
using TabloWindows.ViewModels;

namespace TabloWindows;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    // Saved chrome state for restoring from full-screen.
    private WindowState _preFsState;
    private WindowStyle _preFsStyle;
    private ResizeMode _preFsResize;
    private bool _isFullScreen;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        // Pre-fill the remembered password (PasswordBox can't be data-bound).
        if (!string.IsNullOrEmpty(_vm.SavedPassword))
            PasswordBox.Password = _vm.SavedPassword;

        // The VideoView must be handed the MediaPlayer in code — it isn't set via binding.
        Loaded += async (_, _) =>
        {
            App.Trace("Window Loaded: setting VideoView.MediaPlayer");
            VideoView.MediaPlayer = _vm.Player.MediaPlayer;
            for (var i = 0; i < MultiViewViewModel.MaxPanes; i++)
                MultiViewVideo(i).MediaPlayer = _vm.MultiView.Panes[i].Player;
            App.Trace("VideoView.MediaPlayer set; auto-login begin");
            try { await _vm.TryAutoLoginAsync(); App.Trace("auto-login returned"); }
            catch (Exception ex) { App.Trace("auto-login threw: " + ex); }
            await CheckForUpdateAsync();
        };
        Closing += OnClosing;

        KeyDown += OnKeyDown;
        _vm.MultiView.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MultiViewViewModel.PaneCount)) LayoutMultiView(_vm.MultiView.PaneCount);
            if (e.PropertyName == nameof(MultiViewViewModel.IsOpen))
            {
                if (!_vm.MultiView.IsOpen) ExitFullScreen();
                else RefreshVideoSurface();
            }
        };
        // Preview, so the player's keys win over whichever overlay button has keyboard focus:
        // otherwise the arrows just walk focus along the button row and Space/Enter press it.
        PreviewKeyDown += OnPlayerKeyDown;
        VideoView.MouseDoubleClick += (_, _) => ToggleFullScreen();
    }

    // ---- full-screen video ----

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { ToggleFullScreen(); e.Handled = true; }
        else if (e.Key == Key.Escape && _isFullScreen) { ExitFullScreen(); e.Handled = true; }
    }

    /// <summary>
    /// Remote-style keys while something is playing: Space pauses, Left/Right skip 10 seconds
    /// (live too - the Tablo buffers everything since the channel was tuned), and on a live
    /// channel Up/Down change channel, with Enter jumping straight to the one picked.
    /// </summary>
    private async void OnPlayerKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.MultiView.IsOpen)
        {
            OnMultiViewKeyDown(e);
            return;
        }
        if (!_vm.PlayerOpen) return;
        switch (e.Key)
        {
            case Key.Space:
                _vm.Player.PlayPause();
                break;
            case Key.Enter:
                if (_vm.ChannelChangePending) await _vm.ConfirmChannelAsync();
                else _vm.Player.PlayPause();
                break;
            case Key.Left:
                _vm.Player.Seek(-10_000);
                break;
            case Key.Right:
                _vm.Player.Seek(+10_000);
                break;
            case Key.Up when _vm.Player.IsLive:
                e.Handled = true;
                await _vm.StepChannelAsync(+1);
                return;
            case Key.Down when _vm.Player.IsLive:
                e.Handled = true;
                await _vm.StepChannelAsync(-1);
                return;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Multi-view: ← → step the sound between panes, 1-4 pick a pane directly.</summary>
    private void OnMultiViewKeyDown(KeyEventArgs e)
    {
        var mv = _vm.MultiView;
        switch (e.Key)
        {
            case Key.Left: mv.StepSound(-1); break;
            case Key.Right: mv.StepSound(+1); break;
            case >= Key.D1 and <= Key.D4: mv.SetSound(e.Key - Key.D1); break;
            case >= Key.NumPad1 and <= Key.NumPad4: mv.SetSound(e.Key - Key.NumPad1); break;
            default: return;
        }
        e.Handled = true;
    }

    private LibVLCSharp.WPF.VideoView MultiViewVideo(int i) => i switch
    {
        0 => MvVideo0, 1 => MvVideo1, 2 => MvVideo2, _ => MvVideo3
    };

    private Border MultiViewPane(int i) => i switch
    {
        0 => MvPane0, 1 => MvPane1, 2 => MvPane2, _ => MvPane3
    };

    /// <summary>
    /// Place the panes on the 2x4 layout grid: two side by side, three as two above one centred
    /// below, four as a 2x2. Every pane spans two of the four columns, which is what lets the third
    /// of three sit in the middle.
    /// </summary>
    private void LayoutMultiView(int count)
    {
        (int Row, int Col, int RowSpan)[] cells = count switch
        {
            2 => [(0, 0, 2), (0, 2, 2)],
            3 => [(0, 0, 1), (0, 2, 1), (1, 1, 1)],
            _ => [(0, 0, 1), (0, 2, 1), (1, 0, 1), (1, 2, 1)],
        };
        for (var i = 0; i < cells.Length; i++)
        {
            var pane = MultiViewPane(i);
            Grid.SetRow(pane, cells[i].Row);
            Grid.SetColumn(pane, cells[i].Col);
            Grid.SetRowSpan(pane, cells[i].RowSpan);
            Grid.SetColumnSpan(pane, 2);
        }
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        // Only meaningful while the player is showing.
        if (!_vm.PlayerOpen && !_vm.MultiView.IsOpen) return;
        if (_isFullScreen) ExitFullScreen(); else EnterFullScreen();
    }

    private void EnterFullScreen()
    {
        if (_isFullScreen) return;
        _preFsState = WindowState;
        _preFsStyle = WindowStyle;
        _preFsResize = ResizeMode;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Normal;   // toggle so Maximized re-applies over the taskbar
        WindowState = WindowState.Maximized;
        _isFullScreen = true;
        RefreshVideoSurface();
    }

    private void ExitFullScreen()
    {
        if (!_isFullScreen) return;
        WindowStyle = _preFsStyle;
        ResizeMode = _preFsResize;
        WindowState = _preFsState;
        _isFullScreen = false;
        RefreshVideoSurface();
    }

    /// <summary>
    /// After a window restyle the native LibVLC video surface can be left with stale white or
    /// black pixels in the letterbox area — the hosted HWND doesn't repaint its own background
    /// when WPF re-sizes it. Nudging the VideoView (hide → relayout → show) forces it to
    /// reposition and repaint at the new size.
    ///
    /// It has to happen more than once. A single nudge queued right after the restyle often
    /// runs *before* Windows has finished resizing the window, so it repaints at the old size
    /// and the stale pixels come straight back — which is why the white box still turned up
    /// intermittently. Instead we nudge now and again once the window has actually settled at
    /// its new size, with a backstop nudge for the case where SizeChanged never fires (toggling
    /// full-screen while already maximized).
    /// </summary>
    private void RefreshVideoSurface()
    {
        NudgeVideoView();

        // Re-nudge on the next size change, once only.
        void OnSized(object? s, SizeChangedEventArgs e)
        {
            SizeChanged -= OnSized;
            NudgeVideoView();
        }
        SizeChanged -= OnSized;
        SizeChanged += OnSized;

        var backstop = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(250) };
        backstop.Tick += (_, _) =>
        {
            backstop.Stop();
            SizeChanged -= OnSized;
            NudgeVideoView();
        };
        backstop.Start();
    }

    private void NudgeVideoView()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var views = new List<LibVLCSharp.WPF.VideoView> { VideoView };
            if (_vm.MultiView.IsOpen)
                for (var i = 0; i < _vm.MultiView.PaneCount; i++) views.Add(MultiViewVideo(i));
            foreach (var view in views)
            {
                view.Visibility = Visibility.Hidden;
                view.UpdateLayout();
                view.Visibility = Visibility.Visible;
                view.InvalidateVisual();
            }

            // Multi-view panes also keep stale pixels in their letterbox after a resize until VLC lays
            // out its picture again, which a momentary aspect-ratio change forces. Off the UI thread:
            // these are calls into the players.
            if (_vm.MultiView.IsOpen)
            {
                var players = _vm.MultiView.Panes.Where(p => p.InUse).Select(p => p.Player).ToList();
                Task.Run(async () =>
                {
                    foreach (var player in players)
                        try { player.AspectRatio = "16:10"; } catch { /* ignore */ }
                    await Task.Delay(80);
                    foreach (var player in players)
                        try { player.AspectRatio = null; } catch { /* ignore */ }
                });
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    // ---- auto-update ----

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var info = await _vm.Updater.CheckAsync();
            if (info is null) return;

            var msg = $"A new version of Tablo for Windows is available: v{info.Version}\n" +
                      $"(you have v{UpdateService.CurrentVersion})\n\n" +
                      (string.IsNullOrWhiteSpace(info.Notes) ? "" : Trim(info.Notes!) + "\n\n") +
                      "Download and install it now?";
            if (MessageBox.Show(msg, "Tablo for Windows — Update available",
                    MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
                return;

            Cursor = Cursors.Wait;
            var path = await _vm.Updater.DownloadMsiAsync(info);
            Cursor = Cursors.Arrow;
            if (path is null)
            {
                MessageBox.Show("Could not download the update.", "Tablo for Windows",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            UpdateService.RunInstaller(path);
            Application.Current.Shutdown();
        }
        catch { /* update is best-effort; never block the app */ }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;

    private void SignIn_Click(object sender, RoutedEventArgs e) => _ = DoSignIn();

    private async Task DoSignIn()
    {
        SignInButton.IsEnabled = false;
        try { await _vm.LoginAsync(PasswordBox.Password); }
        finally { SignInButton.IsEnabled = true; }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Detach the player from the VideoView before libvlc is torn down to avoid a shutdown hang.
        VideoView.MediaPlayer = null;
        for (var i = 0; i < MultiViewViewModel.MaxPanes; i++) MultiViewVideo(i).MediaPlayer = null;
        _vm.Dispose();
    }
}
