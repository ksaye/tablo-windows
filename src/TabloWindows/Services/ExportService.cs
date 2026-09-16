using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using LibVLCSharp.Shared;
using TabloWindows.Models;

namespace TabloWindows.Services;

public enum ExportState { Queued, Running, Completed, Failed, Cancelled }

/// <summary>
/// One export job: pulls a recording's HLS stream and remuxes it (stream copy, no
/// re-encode) to a local .ts file that Plex/VLC play directly.
/// </summary>
public sealed partial class ExportJob : ObservableObject
{
    public string RecordingPath { get; init; } = "";
    public string Title { get; init; } = "";
    public string OutputPath { get; init; } = "";

    [ObservableProperty] private ExportState _state = ExportState.Queued;
    [ObservableProperty] private double _progress;          // 0..1
    [ObservableProperty] private string _status = "Queued";

    internal MediaPlayer? Player;
    internal CancellationTokenSource? Cts;

    public string StateText => State switch
    {
        ExportState.Queued => "Queued",
        ExportState.Running => $"{Progress * 100:0}%",
        ExportState.Completed => "Completed",
        ExportState.Failed => "Failed",
        ExportState.Cancelled => "Cancelled",
        _ => ""
    };

    partial void OnStateChanged(ExportState value) => OnPropertyChanged(nameof(StateText));
    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(StateText));
}

public sealed class ExportService
{
    private readonly LibVLC _libvlc;
    private readonly TabloClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1); // one export at a time (one tuner)

    public ExportService(LibVLC libvlc, TabloClient client)
    {
        _libvlc = libvlc;
        _client = client;
    }

    public async Task RunAsync(ExportJob job)
    {
        await _gate.WaitAsync();
        try
        {
            job.Cts = new CancellationTokenSource();
            job.State = ExportState.Running;
            job.Status = "Starting stream…";

            var watch = await _client.WatchAsync(job.RecordingPath, job.Cts.Token);
            if (watch?.PlaylistUrl is null)
            {
                job.State = ExportState.Failed;
                job.Status = "Could not start stream";
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(job.OutputPath)!);
            var dst = job.OutputPath.Replace("'", "");

            using var media = new Media(_libvlc, new Uri(watch.PlaylistUrl));
            // Stream-copy to a TS container on disk.
            media.AddOption(":sout=#standard{access=file,mux=ts,dst='" + dst + "'}");
            media.AddOption(":sout-keep");
            media.AddOption(":no-sout-all");

            using var mp = new MediaPlayer(media);
            job.Player = mp;
            var tcs = new TaskCompletionSource<bool>();

            mp.PositionChanged += (_, e) =>
            {
                job.Progress = Math.Clamp(e.Position, 0, 1);
                job.Status = $"{e.Position * 100:0}%";
            };
            mp.EndReached += (_, _) => tcs.TrySetResult(true);
            mp.EncounteredError += (_, _) => tcs.TrySetException(new Exception("Playback error during export"));

            using var reg = job.Cts.Token.Register(() =>
            {
                tcs.TrySetCanceled();
            });

            mp.Play();
            try
            {
                await tcs.Task;
                job.Progress = 1;
                job.State = ExportState.Completed;
                job.Status = "Completed";
            }
            catch (OperationCanceledException)
            {
                job.State = ExportState.Cancelled;
                job.Status = "Cancelled";
            }
            catch (Exception ex)
            {
                job.State = ExportState.Failed;
                job.Status = ex.Message;
            }
            finally
            {
                mp.Stop();
                job.Player = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Cancel(ExportJob job) => job.Cts?.Cancel();
}
