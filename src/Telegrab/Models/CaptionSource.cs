namespace Telegrab.Models;

/// <summary>
/// Asal deskripsi (caption) sebuah media. Dipetakan ke kolom <c>caption_source</c>
/// (TEXT: own|album|reply|inferred|none) pada tabel <c>media</c>.
/// Tipe logika murni — TANPA dependensi MAUI/WTelegram agar bisa dilink ke proyek test.
/// </summary>
public enum CaptionSource
{
    /// <summary>Caption pada pesan yang sama dengan media.</summary>
    Own,

    /// <summary>Caption diambil dari anggota album (group_id sama).</summary>
    Album,

    /// <summary>Deskripsi berasal dari pesan teks yang membalas (reply) media.</summary>
    Reply,

    /// <summary>Deskripsi ditebak dari teks berdekatan (heuristik adjacency).</summary>
    Inferred,

    /// <summary>Tidak ada deskripsi yang memenuhi syarat.</summary>
    None
}
