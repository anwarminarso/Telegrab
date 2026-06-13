namespace Telegrab.Models;

/// <summary>
/// Hasil validasi sebuah kandidat root download (lihat design.md — ConfigService).
///
/// Membawa status valid/tidak, pesan error yang ramah pengguna, serta flag spesifik
/// agar UI dapat membedakan jenis kegagalan:
/// <list type="bullet">
///   <item><see cref="CannotCreate"/> — folder tidak ada dan tidak dapat dibuat.</item>
///   <item><see cref="NotWritable"/> — folder ada/terbuat tetapi uji-tulis gagal (izin).</item>
/// </list>
///
/// Tipe logika murni — TANPA dependensi MAUI/WTelegram agar dapat dilink ke proyek test.
/// </summary>
public sealed class RootValidationResult
{
    /// <summary>True bila root ada/terbuat DAN dapat ditulisi.</summary>
    public bool IsValid { get; init; }

    /// <summary>Pesan penjelas bila tidak valid; null bila valid.</summary>
    public string? Error { get; init; }

    /// <summary>True bila folder tidak ada dan gagal dibuat (izin/jalur tidak valid).</summary>
    public bool CannotCreate { get; init; }

    /// <summary>True bila folder ada/terbuat tetapi uji-tulis gagal (izin).</summary>
    public bool NotWritable { get; init; }

    /// <summary>Hasil valid.</summary>
    public static RootValidationResult Valid() => new() { IsValid = true };

    /// <summary>Folder tidak dapat dibuat.</summary>
    public static RootValidationResult CannotCreateRoot(string error) =>
        new() { IsValid = false, CannotCreate = true, Error = error };

    /// <summary>Folder tidak dapat ditulisi.</summary>
    public static RootValidationResult NotWritableRoot(string error) =>
        new() { IsValid = false, NotWritable = true, Error = error };

    /// <summary>Kegagalan validasi umum (mis. path kosong).</summary>
    public static RootValidationResult Invalid(string error) =>
        new() { IsValid = false, Error = error };
}
