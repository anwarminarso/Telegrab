namespace Telegrab.Models;

/// <summary>
/// Representasi MURNI sebuah media untuk keperluan <c>CaptionResolver</c> — TANPA dependensi
/// MAUI/WTelegram (mis. tidak memakai <c>ImageSource</c>), sehingga dapat dilink & diuji di
/// proyek test (net10.0).
///
/// Field input hanya <see cref="MediaId"/>. Sisanya adalah OUTPUT yang diisi resolver:
/// <see cref="Caption"/>, <see cref="CaptionSource"/>, <see cref="CaptionFromMessageId"/>,
/// <see cref="Note"/>, <see cref="NoteFromMessageId"/>.
///
/// Sisi MAUI (task 8) meng-adaptasi <c>MediaPart</c> ke/dari tipe ini.
/// </summary>
public sealed class ResolverMedia
{
    /// <summary>Id unik media di Telegram (stabil antar sesi).</summary>
    public long MediaId { get; set; }

    // --- Output (diisi CaptionResolver) -----------------------------------

    /// <summary>Deskripsi efektif media.</summary>
    public string? Caption { get; set; }

    /// <summary>Asal deskripsi.</summary>
    public CaptionSource CaptionSource { get; set; } = CaptionSource.None;

    /// <summary>Id pesan asal caption.</summary>
    public int? CaptionFromMessageId { get; set; }

    /// <summary>Catatan tambahan (case 8) yang bukan caption utama.</summary>
    public string? Note { get; set; }

    /// <summary>Id pesan asal catatan tambahan.</summary>
    public int? NoteFromMessageId { get; set; }
}
