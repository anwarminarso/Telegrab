namespace Telegrab.Models;

/// <summary>
/// DTO yang mencerminkan satu baris tabel <c>media</c> di SQLite (lihat design.md).
/// Dipakai oleh <c>ManifestDbService.Mark</c>/<c>QueryFolder</c> dan
/// <c>DocumentationService</c>.
///
/// Path disimpan RELATIF terhadap root (<see cref="RelativePath"/>); path absolut
/// diturunkan dari <c>root + RelativePath</c> saat dibutuhkan.
///
/// Tipe logika murni — TANPA dependensi MAUI/WTelegram agar bisa dilink ke proyek test.
/// </summary>
public sealed class MediaRecord
{
    // --- Kunci & lokasi ----------------------------------------------------

    /// <summary>Path relatif terhadap root download.</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>Nama file media.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Ukuran file dalam byte.</summary>
    public long Size { get; set; }

    /// <summary>Tipe media: <c>Photo</c> | <c>Video</c> | <c>File</c>.</summary>
    public string Type { get; set; } = string.Empty;

    // --- Metadata teknis ---------------------------------------------------

    /// <summary>Lebar (px) bila tersedia.</summary>
    public int? Width { get; set; }

    /// <summary>Tinggi (px) bila tersedia.</summary>
    public int? Height { get; set; }

    /// <summary>Durasi video dalam detik bila tersedia.</summary>
    public double? DurationSeconds { get; set; }

    /// <summary>Nama/identitas pengirim untuk tampilan.</summary>
    public string? Sender { get; set; }

    // --- Deskripsi ---------------------------------------------------------

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

    // --- Waktu (UTC ISO 8601 di DB) ---------------------------------------

    /// <summary>Tanggal pesan asal (UTC).</summary>
    public DateTime MessageDateUtc { get; set; }

    /// <summary>Waktu media dicatat sebagai terunduh (UTC).</summary>
    public DateTime DownloadedAtUtc { get; set; }

    // --- Identitas chat/pesan (kunci utama) -------------------------------

    /// <summary>Id chat.</summary>
    public long ChatId { get; set; }

    /// <summary>Id pesan.</summary>
    public int MessageId { get; set; }

    /// <summary>Id unik media di Telegram.</summary>
    public long MediaId { get; set; }

    /// <summary>Id album (null bila bukan bagian album).</summary>
    public long? GroupId { get; set; }

    // --- Denormalisasi untuk header dokumentasi ---------------------------

    /// <summary>Judul chat (denormalisasi).</summary>
    public string? ChatTitle { get; set; }

    /// <summary>Judul topik (null bila bukan forum/topik).</summary>
    public string? TopicTitle { get; set; }
}
