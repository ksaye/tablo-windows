using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TabloWindows.Services;

namespace TabloWindows.ViewModels;

public sealed partial class ExportsViewModel : ObservableObject
{
    private readonly ExportService _service;

    public ObservableCollection<ExportJob> Jobs { get; } = new();

    public ExportsViewModel(ExportService service) => _service = service;

    public Task EnqueueAsync(ExportJob job)
    {
        Jobs.Insert(0, job);
        // Fire-and-forget; the service serialises jobs on one tuner internally.
        _ = Task.Run(() => _service.RunAsync(job));
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Cancel(ExportJob? job)
    {
        if (job is null) return;
        _service.Cancel(job);
    }

    [RelayCommand]
    private void OpenFolder(ExportJob? job)
    {
        if (job is null || !File.Exists(job.OutputPath)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{job.OutputPath}\"")
        { UseShellExecute = true });
    }

    [RelayCommand]
    private void Clear()
    {
        for (int i = Jobs.Count - 1; i >= 0; i--)
            if (Jobs[i].State is ExportState.Completed or ExportState.Failed or ExportState.Cancelled)
                Jobs.RemoveAt(i);
    }
}
