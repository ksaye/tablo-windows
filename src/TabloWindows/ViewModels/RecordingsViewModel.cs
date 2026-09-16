using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TabloWindows.Services;

namespace TabloWindows.ViewModels;

public sealed partial class RecordingsViewModel : ObservableObject
{
    private readonly TabloClient _client;
    private readonly Func<ExportJob, Task> _enqueueExport;
    private readonly Func<string, string, bool, Task> _play;

    public ObservableCollection<RecordingItem> Items { get; } = new();

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private RecordingItem? _selected;

    public RecordingsViewModel(TabloClient client,
        Func<string, string, bool, Task> play,
        Func<ExportJob, Task> enqueueExport)
    {
        _client = client;
        _play = play;
        _enqueueExport = enqueueExport;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        StatusMessage = "Loading recordings…";
        try
        {
            var recs = await _client.GetRecordingsAsync();
            var rows = recs.Select(r => new RecordingItem(r, _client))
                .OrderByDescending(r => r.StartedUtc)
                .ToList();

            Items.Clear();
            foreach (var row in rows) Items.Add(row);

            StatusMessage = Items.Count == 0 ? "No recordings found." : $"{Items.Count} recordings";
        }
        catch (Exception ex)
        {
            StatusMessage = "Failed to load recordings: " + ex.Message;
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task Play(RecordingItem? item)
    {
        item ??= Selected;
        if (item is null) return;

        await _play(item.Path, item.Title, false);
    }

    [RelayCommand]
    private async Task Delete(RecordingItem? item)
    {
        item ??= Selected;
        if (item is null) return;

        if (MessageBox.Show($"Delete “{item.Owner}”?\nThis removes it from the Tablo permanently.",
                "Delete recording", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            await _client.DeleteRecordingAsync(item.Path);
            Items.Remove(item);
            StatusMessage = $"{Items.Count} recordings";
        }
        catch (Exception ex)
        {
            MessageBox.Show("Delete failed: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task Export(RecordingItem? item)
    {
        item ??= Selected;
        if (item is null) return;
        var safe = string.Join("_", item.Owner.Split(Path.GetInvalidFileNameChars()));
        var dlg = new SaveFileDialog
        {
            Title = "Export recording",
            FileName = safe + ".ts",
            Filter = "MPEG-TS video (*.ts)|*.ts",
            DefaultExt = ".ts"
        };
        if (dlg.ShowDialog() != true) return;

        var job = new ExportJob
        {
            RecordingPath = item.Path,
            Title = item.Title,
            OutputPath = dlg.FileName
        };
        await _enqueueExport(job);
    }
}
