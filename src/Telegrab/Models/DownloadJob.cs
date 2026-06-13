using CommunityToolkit.Mvvm.ComponentModel;

namespace Telegrab.Models;

/// <summary>Status sebuah pekerjaan unduh dalam antrian.</summary>
public enum DownloadState
{
    Queued,
    Downloading,
    FloodWait,
    Completed,
    Skipped,
    Failed,
    Canceled
}

/// <summary>Satu pekerjaan unduh di dalam antrian batch.</summary>
public partial class DownloadJob : ObservableObject
{
    public required MediaPart Part { get; init; }
    public required DownloadContext Context { get; init; }

    /// <summary>Pembatalan khusus job ini.</summary>
    public CancellationTokenSource Cts { get; } = new();

    public string FileName => Part.FileName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private DownloadState _state = DownloadState.Queued;

    [ObservableProperty] private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private int _floodWaitSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _error;

    public bool IsActive => State is DownloadState.Downloading or DownloadState.FloodWait;
    public bool CanCancel => State is DownloadState.Queued or DownloadState.Downloading or DownloadState.FloodWait;

    public string StatusText => State switch
    {
        DownloadState.Queued => "Queued",
        DownloadState.Downloading => "Downloading...",
        DownloadState.FloodWait => $"Rate limited, waiting {FloodWaitSeconds}s...",
        DownloadState.Completed => "Done",
        DownloadState.Skipped => "Already downloaded",
        DownloadState.Failed => $"Failed: {Error}",
        DownloadState.Canceled => "Canceled",
        _ => string.Empty
    };
}
