using System.Collections.ObjectModel;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using Telegrab.Models;

namespace Telegrab.Services;

/// <summary>
/// Antrian unduh batch. Memproses pekerjaan secara berurutan (satu per satu) agar ramah
/// terhadap rate limit Telegram, dengan penanganan FLOOD_WAIT di lapisan TelegramService.
/// Melewati file yang sudah tercatat di manifest, dan menata folder/nama file otomatis.
/// </summary>
public partial class DownloadQueueService : ObservableObject
{
    private readonly TelegramService _telegram;
    private readonly DownloadManifestService _manifest;
    private readonly ConfigService _config;

    private readonly Channel<DownloadJob> _channel = Channel.CreateUnbounded<DownloadJob>();
    private Task? _worker;
    private readonly object _workerGate = new();

    private CancellationTokenSource? _autoClearCts;
    private const int AutoClearDelaySeconds = 4;

    /// <summary>Semua job (antri/proses/selesai) untuk ditampilkan di UI.</summary>
    public ObservableCollection<DownloadJob> Jobs { get; } = new();

    [ObservableProperty] private bool _hasJobs;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _summary = string.Empty;

    public DownloadQueueService(TelegramService telegram, DownloadManifestService manifest, ConfigService config)
    {
        _telegram = telegram;
        _manifest = manifest;
        _config = config;
    }

    private string Root
    {
        get
        {
            var folder = _config.Load().DownloadFolder;
            return string.IsNullOrWhiteSpace(folder) ? _config.DefaultDownloadFolder : folder;
        }
    }

    /// <summary>Tambahkan media ke antrian. Media yang sudah diunduh dilewati otomatis.</summary>
    public int Enqueue(IEnumerable<MediaPart> parts, DownloadContext ctx)
    {
        int added = 0;
        foreach (var part in parts)
        {
            if (part.IsDownloaded) continue;
            if (_manifest.IsDownloaded(ctx.ChatId, part.MessageId, part.MediaId, out var existing))
            {
                part.IsDownloaded = true;
                part.LocalPath = existing;
                continue;
            }
            // Hindari duplikat job yang masih aktif untuk media yang sama.
            if (Jobs.Any(j => ReferenceEquals(j.Part, part) && j.CanCancel)) continue;

            var job = new DownloadJob { Part = part, Context = ctx };
            Jobs.Add(job);
            _channel.Writer.TryWrite(job);
            added++;
        }

        if (added > 0)
        {
            RefreshSummary();
            EnsureWorker();
        }
        return added;
    }

    /// <summary>Bersihkan job yang sudah selesai/gagal/dibatalkan dari daftar.</summary>
    public void ClearFinished()
    {
        for (int i = Jobs.Count - 1; i >= 0; i--)
        {
            if (!Jobs[i].CanCancel)
                Jobs.RemoveAt(i);
        }
        RefreshSummary();
    }

    /// <summary>
    /// Hanya hapus job yang sukses/dilewati/dibatalkan; sisakan yang GAGAL agar tetap terlihat.
    /// Dipakai oleh auto-clear ketika antrian selesai.
    /// </summary>
    public void ClearCompleted()
    {
        for (int i = Jobs.Count - 1; i >= 0; i--)
        {
            var s = Jobs[i].State;
            if (s is DownloadState.Completed or DownloadState.Skipped or DownloadState.Canceled)
                Jobs.RemoveAt(i);
        }
        RefreshSummary();
    }

    /// <summary>Batalkan semua job yang masih antri / sedang berjalan.</summary>
    public void CancelAll()
    {
        foreach (var job in Jobs)
        {
            if (job.CanCancel) job.Cts.Cancel();
        }
    }

    public void Cancel(DownloadJob job)
    {
        if (job.CanCancel) job.Cts.Cancel();
    }

    private void EnsureWorker()
    {
        lock (_workerGate)
        {
            if (_worker == null || _worker.IsCompleted)
                _worker = Task.Run(ProcessLoopAsync);
        }
    }

    private async Task ProcessLoopAsync()
    {
        await foreach (var job in _channel.Reader.ReadAllAsync())
        {
            await ProcessJobAsync(job);
        }
    }

    private async Task ProcessJobAsync(DownloadJob job)
    {
        if (job.Cts.IsCancellationRequested)
        {
            UpdateJob(job, j => j.State = DownloadState.Canceled);
            RefreshSummary();
            return;
        }

        UpdateJob(job, j => { j.State = DownloadState.Downloading; j.Progress = 0; });
        SetRunning(true);
        RefreshSummary();

        try
        {
            await DownloadOneAsync(job.Part, job.Context, new ProgressTo(job, this), job, job.Cts.Token);
            UpdateJob(job, j => { j.State = DownloadState.Completed; j.Progress = 1; });
        }
        catch (OperationCanceledException)
        {
            UpdateJob(job, j => j.State = DownloadState.Canceled);
        }
        catch (Exception ex)
        {
            UpdateJob(job, j => { j.State = DownloadState.Failed; j.Error = ex.Message; });
        }
        finally
        {
            if (!Jobs.Any(j => j.IsActive || j.State == DownloadState.Queued))
                SetRunning(false);
            RefreshSummary();
        }
    }

    /// <summary>
    /// Unduh satu media (sumber kebenaran tunggal). Memeriksa manifest, menata path,
    /// mengunduh dengan FLOOD_WAIT handling, lalu mencatat ke manifest. Mengembalikan path lokal.
    /// Dipakai oleh worker antrian maupun unduhan langsung (tombol/viewer).
    /// </summary>
    public async Task<string?> DownloadOneAsync(
        MediaPart part,
        DownloadContext ctx,
        IProgress<double>? progress = null,
        DownloadJob? job = null,
        CancellationToken ct = default)
    {
        // Sudah ada? lewati (idempotent).
        if (_manifest.IsDownloaded(ctx.ChatId, part.MessageId, part.MediaId, out var existing))
        {
            ApplyDownloaded(part, existing);
            if (job != null) UpdateJob(job, j => j.State = DownloadState.Skipped);
            return existing;
        }

        var target = _telegram.BuildTargetPath(Root, ctx, part);

        void OnFlood(int seconds)
        {
            if (job != null) UpdateJob(job, j => { j.State = DownloadState.FloodWait; j.FloodWaitSeconds = seconds; });
        }

        var path = await _telegram.DownloadToPathAsync(part, target, progress, OnFlood, ct);

        _manifest.Mark(ctx, part, path);
        ApplyDownloaded(part, path);
        return path;
    }

    private static void ApplyDownloaded(MediaPart part, string path)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            part.LocalPath = path;
            part.IsDownloaded = true;
            part.Progress = 1;
        });
    }

    private static void UpdateJob(DownloadJob job, Action<DownloadJob> apply)
        => MainThread.BeginInvokeOnMainThread(() => apply(job));

    private void SetRunning(bool value)
        => MainThread.BeginInvokeOnMainThread(() => IsRunning = value);

    private void RefreshSummary()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            int done = Jobs.Count(j => j.State is DownloadState.Completed or DownloadState.Skipped);
            int failed = Jobs.Count(j => j.State == DownloadState.Failed);
            int pending = Jobs.Count(j => j.State == DownloadState.Queued);
            int active = Jobs.Count(j => j.IsActive);

            HasJobs = Jobs.Count > 0;

            var parts = new List<string>();
            if (active > 0) parts.Add($"{active} running");
            if (pending > 0) parts.Add($"{pending} queued");
            if (done > 0) parts.Add($"{done} done");
            if (failed > 0) parts.Add($"{failed} failed");
            Summary = parts.Count > 0 ? string.Join(" · ", parts) : string.Empty;

            // Antrian selesai (tidak ada yang berjalan/antri) & ada job yang bisa dibersihkan:
            // jadwalkan auto-clear agar panel menutup sendiri. Job GAGAL tetap dipertahankan.
            bool idle = active == 0 && pending == 0;
            bool hasClearable = Jobs.Any(j =>
                j.State is DownloadState.Completed or DownloadState.Skipped or DownloadState.Canceled);

            if (idle && hasClearable) ScheduleAutoClear();
            else CancelAutoClear();
        });
    }

    private void CancelAutoClear()
    {
        _autoClearCts?.Cancel();
        _autoClearCts = null;
    }

    private void ScheduleAutoClear()
    {
        CancelAutoClear();
        _autoClearCts = new CancellationTokenSource();
        var token = _autoClearCts.Token;

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(AutoClearDelaySeconds), token); }
            catch (OperationCanceledException) { return; }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (token.IsCancellationRequested) return;
                // Pastikan masih idle sebelum membersihkan (ada kemungkinan job baru masuk).
                if (Jobs.Any(j => j.IsActive || j.State == DownloadState.Queued)) return;
                ClearCompleted();
            });
        });
    }

    /// <summary>Adapter IProgress yang memperbarui job & MediaPart di main thread.</summary>
    private sealed class ProgressTo : IProgress<double>
    {
        private readonly DownloadJob _job;
        private readonly DownloadQueueService _owner;
        public ProgressTo(DownloadJob job, DownloadQueueService owner) { _job = job; _owner = owner; }

        public void Report(double value)
            => MainThread.BeginInvokeOnMainThread(() =>
            {
                _job.Progress = value;
                _job.Part.Progress = value;
            });
    }
}
