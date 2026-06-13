using CommunityToolkit.Mvvm.ComponentModel;
using TL;

namespace Telegrab.Models;

/// <summary>Satu item media (foto/video/file). Sebuah pesan album bisa punya beberapa MediaPart.</summary>
public partial class MediaPart : ObservableObject
{
    public MediaKind Kind { get; init; } = MediaKind.None;
    public bool IsVideo => Kind == MediaKind.Video;
    public string FileName { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public string SizeText => FileSize > 0 ? FormatSize(FileSize) : string.Empty;

    public Photo? Photo { get; init; }
    public Document? Document { get; init; }

    /// <summary>Id pesan asal media ini (untuk penamaan & kunci manifest).</summary>
    public int MessageId { get; set; }

    /// <summary>Tanggal pesan asal (untuk prefiks nama file).</summary>
    public DateTime MessageDate { get; set; }

    /// <summary>Id unik media di Telegram (id foto atau dokumen). Stabil antar sesi.</summary>
    public long MediaId => Photo?.id ?? Document?.id ?? 0;

    [ObservableProperty] private ImageSource? _thumbnail;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isDownloaded;
    [ObservableProperty] private string? _localPath;

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double size = bytes;
        int u = 0;
        while (size >= 1024 && u < units.Length - 1) { size /= 1024; u++; }
        return $"{size:0.#} {units[u]}";
    }
}
