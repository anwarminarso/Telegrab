using System.IO;
using System.Text.Json;
using Telegrab.Models;

namespace Telegrab.Services;

/// <summary>
/// Manifest persisten berisi daftar media yang sudah berhasil diunduh.
/// Disimpan sebagai JSON di folder data aplikasi. Dipakai agar batch download
/// tidak mengunduh ulang file yang sudah ada (resume-friendly antar sesi).
/// </summary>
public class DownloadManifestService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _manifestPath;
    private readonly object _gate = new();
    private readonly Dictionary<string, ManifestEntry> _entries = new();

    public DownloadManifestService(ConfigService config)
    {
        _manifestPath = Path.Combine(config.AppDataFolder, "downloaded.json");
        Load();
    }

    /// <summary>Bentuk kunci unik dan stabil untuk sebuah media.</summary>
    public static string BuildKey(long chatId, int messageId, long mediaId)
        => $"{chatId}:{messageId}:{mediaId}";

    private void Load()
    {
        if (!File.Exists(_manifestPath)) return;
        try
        {
            var json = File.ReadAllText(_manifestPath);
            var list = JsonSerializer.Deserialize<List<ManifestEntry>>(json);
            if (list == null) return;

            foreach (var e in list)
                _entries[BuildKey(e.ChatId, e.MessageId, e.MediaId)] = e;
        }
        catch
        {
            // Manifest rusak: mulai dari kosong (file akan ditimpa saat penyimpanan berikutnya).
        }
    }

    /// <summary>Apakah media sudah tercatat diunduh DAN file fisiknya masih ada.</summary>
    public bool IsDownloaded(long chatId, int messageId, long mediaId, out string localPath)
    {
        localPath = string.Empty;
        lock (_gate)
        {
            if (_entries.TryGetValue(BuildKey(chatId, messageId, mediaId), out var entry)
                && !string.IsNullOrEmpty(entry.LocalPath) && File.Exists(entry.LocalPath))
            {
                localPath = entry.LocalPath;
                return true;
            }
        }
        return false;
    }

    /// <summary>Catat sebuah media sebagai sudah diunduh dan simpan ke disk.</summary>
    public void Mark(DownloadContext ctx, MediaPart part, string localPath)
    {
        var entry = new ManifestEntry
        {
            ChatId = ctx.ChatId,
            MessageId = part.MessageId,
            MediaId = part.MediaId,
            FileName = Path.GetFileName(localPath),
            LocalPath = localPath,
            Size = part.FileSize,
            DownloadedAt = DateTime.UtcNow
        };

        lock (_gate)
        {
            _entries[BuildKey(ctx.ChatId, part.MessageId, part.MediaId)] = entry;
            Save_NoLock();
        }
    }

    private void Save_NoLock()
    {
        try
        {
            var json = JsonSerializer.Serialize(_entries.Values.ToList(), JsonOptions);
            File.WriteAllText(_manifestPath, json);
        }
        catch
        {
            // Abaikan kegagalan tulis; tidak boleh menggagalkan proses unduh.
        }
    }
}
