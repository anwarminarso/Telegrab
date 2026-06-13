namespace Telegrab.Models;

/// <summary>
/// Representasi MURNI sebuah pesan Telegram untuk keperluan <c>CaptionResolver</c> — TANPA
/// dependensi MAUI/WTelegram, sehingga dapat dilink & diuji di proyek test (net10.0).
///
/// Membawa data yang diperlukan algoritma asosiasi: id pesan, tanggal (UTC), teks, id album
/// (<see cref="GroupId"/>), target reply (<see cref="ReplyToMsgId"/>), identitas pengirim
/// (<see cref="FromId"/>/<see cref="PostAuthor"/>), serta daftar media.
///
/// Sisi MAUI (task 8) meng-adaptasi <c>MessageItem</c>/<c>MediaPart</c> ke tipe ini.
/// </summary>
public sealed class ResolverMessage : ISenderIdentity
{
    /// <summary>Id pesan.</summary>
    public int MessageId { get; set; }

    /// <summary>Tanggal pesan (UTC).</summary>
    public DateTime DateUtc { get; set; }

    /// <summary>Teks pesan (caption media bila pesan memuat media, atau teks murni).</summary>
    public string? Text { get; set; }

    /// <summary>Id album Telegram (null/0 bila bukan bagian album).</summary>
    public long? GroupId { get; set; }

    /// <summary>Id pesan yang dibalas (reply), null bila bukan reply.</summary>
    public int? ReplyToMsgId { get; set; }

    /// <summary>Id pengirim (peer) untuk pembandingan asosiasi lintas pesan.</summary>
    public long? FromId { get; set; }

    /// <summary>Nama penulis post (channel) bila ada.</summary>
    public string? PostAuthor { get; set; }

    /// <summary>Media yang dimuat pesan ini (kosong bila pesan teks murni).</summary>
    public List<ResolverMedia> Media { get; } = new();

    /// <summary>True bila pesan memuat minimal satu media.</summary>
    public bool HasMedia => Media.Count > 0;

    /// <summary>True bila pesan punya teks non-kosong.</summary>
    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    /// <summary>True bila pesan adalah teks murni (punya teks, tanpa media).</summary>
    public bool IsPureText => HasText && !HasMedia;
}
