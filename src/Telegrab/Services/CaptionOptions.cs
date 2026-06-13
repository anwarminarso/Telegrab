namespace Telegrab.Services;

/// <summary>
/// Opsi untuk <see cref="CaptionResolver"/>. Tipe logika MURNI (tanpa MAUI) agar dapat
/// dilink ke proyek test.
///
/// Keputusan terkunci (lihat requirements.md): heuristik <c>inferred</c> aktif secara default,
/// dengan jendela waktu ≤ 60 detik.
/// </summary>
public sealed class CaptionOptions
{
    /// <summary>Apakah heuristik adjacency (<c>inferred</c>, case 3/5/6) diaktifkan.</summary>
    public bool InferredEnabled { get; set; } = true;

    /// <summary>Jendela waktu maksimum (detik) untuk kandidat <c>inferred</c>.</summary>
    public int InferredWindowSeconds { get; set; } = 60;

    /// <summary>Opsi default (inferred aktif, jendela 60 detik).</summary>
    public static CaptionOptions Default => new();
}
