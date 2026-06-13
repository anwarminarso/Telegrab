using System.IO;
using Telegrab.Models;

namespace Telegrab.Services;

/// <summary>
/// Logika MURNI untuk memvalidasi kandidat root download (Requirement 1.1/1.2/3.1/3.2/3.3).
///
/// Tidak bergantung pada MAUI/WTelegram sehingga dapat dilink & diuji langsung di
/// proyek test (net10.0). <c>ConfigService.ValidateRoot</c> mendelegasikan ke sini.
///
/// Strategi (sesuai design.md):
///  1. Buat folder bila belum ada (tangkap kegagalan → <see cref="RootValidationResult.CannotCreate"/>).
///  2. Uji tulis: buat file sementara acak <c>.telegrab_write_test_{guid}</c>, tulis lalu hapus
///     (tangkap <see cref="UnauthorizedAccessException"/>/<see cref="IOException"/> →
///     <see cref="RootValidationResult.NotWritable"/>).
/// </summary>
public static class RootValidator
{
    /// <summary>Prefix nama file uji-tulis sementara.</summary>
    public const string WriteTestPrefix = ".telegrab_write_test_";

    /// <summary>
    /// Validasi sebuah kandidat root: pastikan folder ada/terbuat lalu uji kemampuan tulis.
    /// </summary>
    public static RootValidationResult Validate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return RootValidationResult.Invalid("Folder root belum ditentukan.");

        // 1) Pastikan folder ada; buat bila perlu.
        if (!Directory.Exists(path))
        {
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return RootValidationResult.CannotCreateRoot(
                    $"Folder tidak dapat dibuat: {ex.Message}");
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return RootValidationResult.CannotCreateRoot(
                    $"Jalur folder tidak valid: {ex.Message}");
            }
        }

        // 2) Uji tulis: buat, tulis, lalu hapus file sementara.
        var testFile = Path.Combine(path, WriteTestPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(testFile, "telegrab");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            TryDelete(testFile);
            return RootValidationResult.NotWritableRoot(
                $"Folder tidak dapat ditulisi (periksa izin akses): {ex.Message}");
        }

        TryDelete(testFile);
        return RootValidationResult.Valid();
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
                File.Delete(file);
        }
        catch
        {
            // Best-effort: jangan gagalkan validasi hanya karena pembersihan gagal.
        }
    }
}
