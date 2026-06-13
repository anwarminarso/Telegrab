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
    private readonly ManifestDbService _db;
    private readonly DocumentationService _documentation;

    /// <summary>
    /// Pesan strict gating (Requirement 1.2): bila root belum valid / DB belum siap, semua
    /// operasi unduh ditolak dengan arahan ke modal Configuration.
    /// </summary>
    private const string NotReadyMessage =
        "Folder download belum dikonfigurasi atau tidak valid. Buka Configuration untuk " +
        "mengatur folder download sebelum mengunduh.";

    private readonly Channel<DownloadJob> _channel = Channel.CreateUnbounded<DownloadJob>();
    private Task? _worker;
    private readonly object _workerGate = new();

    private CancellationTokenSource? _autoClearCts;
    private const int AutoClearDelaySeconds = 4;

    // P4: batasi jumlah job "selesai" (Completed/Skipped) yang ditahan di daftar UI agar tidak
    // tumbuh tanpa batas pada batch besar (mis. unduh ribuan media). Job yang dipangkas tetap
    // dihitung lewat _prunedDone sehingga ringkasan tetap akurat.
    private const int MaxFinishedRetained = 100;
    private int _prunedDone;

    // Kunci per-media untuk menserialkan unduhan media yang SAMA (B2). Tanpa ini, worker antrian
    // dan unduhan langsung (tombol/viewer) dapat memproses MediaPart yang sama secara paralel dan
    // sama-sama File.Create() path tujuan yang identik → sharing violation / file saling terhapus
    // (lihat penanganan TryDelete di TelegramService.DownloadToPathAsync). Refcount dipakai agar
    // entri dilepas saat tak ada lagi pemakai (mencegah dictionary tumbuh tanpa batas).
    private readonly object _keyGate = new();
    private readonly Dictionary<string, KeyLock> _keyLocks = new();

    /// <summary>Semua job (antri/proses/selesai) untuk ditampilkan di UI.</summary>
    public ObservableCollection<DownloadJob> Jobs { get; } = new();

    /// <summary>
    /// True bila masih ada pekerjaan unduh yang berjalan atau mengantri (B3). Dipakai untuk
    /// memblokir penggantian root download selama unduhan aktif. Dibaca di UI thread.
    /// </summary>
    public bool HasActiveWork => Jobs.Any(j => j.IsActive || j.State == DownloadState.Queued);

    [ObservableProperty] private bool _hasJobs;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _summary = string.Empty;

    public DownloadQueueService(TelegramService telegram, ManifestDbService db, DocumentationService documentation)
    {
        _telegram = telegram;
        _db = db;
        _documentation = documentation;
    }

    /// <summary>
    /// Root download aktif = root tempat <c>telegrab.db</c> dibuka, sehingga path media
    /// selalu relatif terhadap root yang sama dengan DB-nya. Memanggil properti ini hanya
    /// sah setelah <see cref="EnsureReady"/> (DB siap).
    /// </summary>
    private string Root => _db.Root
        ?? throw new InvalidOperationException(NotReadyMessage);

    /// <summary>
    /// Strict gating (Requirement 1.2): tolak operasi unduh bila DB manifest belum siap
    /// (root belum dikonfigurasi/valid). Melempar <see cref="InvalidOperationException"/>
    /// dengan pesan yang mengarahkan pengguna ke modal Configuration.
    /// </summary>
    private void EnsureReady()
    {
        if (!_db.IsReady)
            throw new InvalidOperationException(NotReadyMessage);
    }

    /// <summary>Tambahkan media ke antrian. Media yang sudah diunduh dilewati otomatis.</summary>
    public int Enqueue(IEnumerable<MediaPart> parts, DownloadContext ctx)
    {
        // Strict gating: tanpa DB siap, tolak seluruh operasi dengan pesan jelas.
        EnsureReady();

        int added = 0;
        foreach (var part in parts)
        {
            if (part.IsDownloaded) continue;
            // MediaId == 0 = sentinel (tak ada id foto/dokumen). Tidak dapat dilacak andal di
            // manifest (kunci chat,message,media bisa bertabrakan), jadi dilewati.
            if (part.MediaId == 0) continue;
            if (_db.IsDownloaded(ctx.ChatId, part.MessageId, part.MediaId, out var existing))
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
        _prunedDone = 0; // view dibersihkan: reset hitungan yang sudah dipangkas (P4)
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
        _prunedDone = 0; // view dibersihkan: reset hitungan yang sudah dipangkas (P4)
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
        // Strict gating: tolak unduhan bila DB manifest belum siap (root belum valid).
        EnsureReady();

        // Serialkan per-media (B2): cegah worker antrian & unduhan langsung memproses media yang
        // sama secara paralel. Kunci memakai identitas manifest (chat, message, media).
        var lockKey = $"{ctx.ChatId}:{part.MessageId}:{part.MediaId}";
        var keyLock = AcquireKeyLock(lockKey);
        bool locked = false;
        try
        {
            await keyLock.Semaphore.WaitAsync(ct);
            locked = true;

            // Sudah ada? lewati (idempotent). Dicek DI DALAM kunci agar pemenang lomba mencatat
            // manifest dan pesaingnya melihatnya sebagai "sudah diunduh" alih-alih menulis ulang.
            if (_db.IsDownloaded(ctx.ChatId, part.MessageId, part.MediaId, out var existing))
            {
                ApplyDownloaded(part, existing);
                if (job != null) UpdateJob(job, j => j.State = DownloadState.Skipped);
                return existing;
            }

            var root = Root;
            var target = _telegram.BuildTargetPath(root, ctx, part);

            void OnFlood(int seconds)
            {
                if (job != null) UpdateJob(job, j => { j.State = DownloadState.FloodWait; j.FloodWaitSeconds = seconds; });
            }

            var path = await _telegram.DownloadToPathAsync(part, target, progress, OnFlood, ct);

            // Catat ke DB manifest (sumber kebenaran). relative_path disimpan relatif terhadap
            // root tempat DB berada, dinormalkan ke pemisah '/' untuk portabilitas.
            var record = BuildRecord(ctx, part, root, path);
            _db.Mark(record);

            // Picu regenerasi README.md untuk folder media (di-debounce agar batch unduh tidak
            // menulis README berkali-kali). Folder diturunkan dari relative_path yang baru dicatat
            // sehingga selalu konsisten dengan lokasi file di disk (Requirement 9.1).
            var relativeFolder = GetRelativeFolder(record.RelativePath);
            _ = _documentation.UpdateFolderAsync(relativeFolder);

            ApplyDownloaded(part, path);
            return path;
        }
        finally
        {
            if (locked) keyLock.Semaphore.Release();
            ReleaseKeyLock(lockKey, keyLock);
        }
    }

    /// <summary>Ambil (atau buat) kunci per-media dan naikkan refcount-nya.</summary>
    private KeyLock AcquireKeyLock(string key)
    {
        lock (_keyGate)
        {
            if (!_keyLocks.TryGetValue(key, out var kl))
            {
                kl = new KeyLock();
                _keyLocks[key] = kl;
            }
            kl.RefCount++;
            return kl;
        }
    }

    /// <summary>Turunkan refcount kunci; lepas &amp; buang saat tak ada lagi pemakai.</summary>
    private void ReleaseKeyLock(string key, KeyLock keyLock)
    {
        lock (_keyGate)
        {
            if (--keyLock.RefCount <= 0)
            {
                _keyLocks.Remove(key);
                keyLock.Semaphore.Dispose();
            }
        }
    }

    /// <summary>Kunci eksklusif per-media (semaphore biner) + refcount untuk pembersihan.</summary>
    private sealed class KeyLock
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int RefCount;
    }

    /// <summary>
    /// Folder RELATIF (terhadap root) dari sebuah <c>relative_path</c>: direktori induk file,
    /// dinormalkan ke pemisah '/'. Mengembalikan string kosong bila file berada langsung di root.
    /// </summary>
    private static string GetRelativeFolder(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var idx = normalized.LastIndexOf('/');
        return idx < 0 ? string.Empty : normalized[..idx];
    }

    /// <summary>
    /// Susun <see cref="MediaRecord"/> dari hasil unduhan. <c>relative_path</c> diturunkan dari
    /// path absolut hasil <see cref="TelegramService.BuildTargetPath"/> relatif terhadap
    /// <paramref name="root"/> (DB root), lalu dinormalkan ke pemisah '/'.
    /// </summary>
    private static MediaRecord BuildRecord(DownloadContext ctx, MediaPart part, string root, string absolutePath)
    {
        var relativePath = Path.GetRelativePath(root, absolutePath).Replace('\\', '/');

        return new MediaRecord
        {
            ChatId = ctx.ChatId,
            MessageId = part.MessageId,
            MediaId = part.MediaId,
            // group_id album diturunkan dari grouped_id pesan (B1) agar DocumentationRenderer
            // dapat menggabungkan anggota album menjadi satu post. null bila bukan bagian album.
            GroupId = part.GroupedId != 0 ? part.GroupedId : null,
            ChatTitle = ctx.ChatTitle,
            TopicTitle = ctx.TopicTitle,
            RelativePath = relativePath,
            FileName = Path.GetFileName(absolutePath),
            Size = part.FileSize,
            Type = MapType(part.Kind),
            Width = part.Width,
            Height = part.Height,
            DurationSeconds = part.DurationSeconds,
            Sender = part.Sender,
            Caption = part.Caption,
            CaptionSource = part.CaptionSource,
            CaptionFromMessageId = part.CaptionFromMessageId,
            Note = part.Note,
            NoteFromMessageId = part.NoteFromMessageId,
            MessageDateUtc = part.MessageDate,
            DownloadedAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>Petakan <see cref="MediaKind"/> ke kolom <c>type</c> (Photo | Video | File).</summary>
    private static string MapType(MediaKind kind) => kind switch
    {
        MediaKind.Photo => "Photo",
        MediaKind.Video => "Video",
        _ => "File",
    };

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
            // Pangkas job selesai yang melebihi batas agar daftar UI tidak tumbuh tanpa batas (P4).
            PruneFinishedOverflow();

            int done = _prunedDone + Jobs.Count(j => j.State is DownloadState.Completed or DownloadState.Skipped);
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

    /// <summary>
    /// Pangkas job selesai (Completed/Skipped) terlama bila jumlahnya melampaui
    /// <see cref="MaxFinishedRetained"/> (P4). Job GAGAL dan dibatalkan dipertahankan agar tetap
    /// terlihat. Setiap job yang dipangkas dihitung di <see cref="_prunedDone"/> supaya ringkasan
    /// tetap akurat. Dipanggil dari <see cref="RefreshSummary"/> (UI thread).
    /// </summary>
    private void PruneFinishedOverflow()
    {
        int finished = 0;
        for (int i = 0; i < Jobs.Count; i++)
            if (Jobs[i].State is DownloadState.Completed or DownloadState.Skipped)
                finished++;

        int overflow = finished - MaxFinishedRetained;
        if (overflow <= 0) return;

        for (int i = 0; i < Jobs.Count && overflow > 0;)
        {
            if (Jobs[i].State is DownloadState.Completed or DownloadState.Skipped)
            {
                Jobs.RemoveAt(i);
                _prunedDone++;
                overflow--;
            }
            else
            {
                i++;
            }
        }
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
