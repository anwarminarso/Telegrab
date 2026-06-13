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

    // --- Metadata teknis (diisi saat mapping di TelegramService) ----------

    /// <summary>Lebar (px) bila tersedia.</summary>
    public int? Width { get; set; }

    /// <summary>Tinggi (px) bila tersedia.</summary>
    public int? Height { get; set; }

    /// <summary>Durasi video dalam detik bila tersedia.</summary>
    public double? DurationSeconds { get; set; }

    /// <summary>Nama/identitas pengirim untuk tampilan.</summary>
    public string? Sender { get; set; }

    // --- Deskripsi (diisi oleh CaptionResolver) ---------------------------

    /// <summary>Deskripsi efektif media.</summary>
    public string? Caption { get; set; }

    /// <summary>Asal deskripsi.</summary>
    public CaptionSource CaptionSource { get; set; } = CaptionSource.None;

    /// <summary>Id pesan asal caption (bila berbeda dari media).</summary>
    public int? CaptionFromMessageId { get; set; }

    /// <summary>Catatan tambahan (case 8) yang bukan caption utama.</summary>
    public string? Note { get; set; }

    /// <summary>Id pesan asal catatan tambahan.</summary>
    public int? NoteFromMessageId { get; set; }

    // --- Identitas untuk resolver -----------------------------------------

    /// <summary>Id pesan yang dibalas (reply) oleh pesan asal media, bila ada.</summary>
    public int? ReplyToMsgId { get; set; }

    /// <summary>Id pengirim (peer) untuk pembandingan asosiasi lintas pesan.</summary>
    public long? FromId { get; set; }

    /// <summary>Nama penulis post (channel) bila ada.</summary>
    public string? PostAuthor { get; set; }

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
