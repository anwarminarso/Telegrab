namespace Telegrab.Models;

/// <summary>Satu catatan media yang sudah berhasil diunduh (disimpan persisten ke JSON).</summary>
public class ManifestEntry
{
    public long ChatId { get; set; }
    public int MessageId { get; set; }
    public long MediaId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime DownloadedAt { get; set; }
}
