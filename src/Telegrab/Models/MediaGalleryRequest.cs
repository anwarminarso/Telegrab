namespace Telegrab.Models;

/// <summary>
/// Permintaan membuka viewer media dengan kemampuan navigasi next/prev.
/// </summary>
public sealed class MediaGalleryRequest
{
    /// <summary>Daftar media yang dapat dilihat (foto/video) dalam urutan tampil.</summary>
    public required IReadOnlyList<MediaPart> Items { get; init; }

    /// <summary>Indeks media yang pertama kali dibuka.</summary>
    public required int StartIndex { get; init; }

    /// <summary>
    /// Memastikan sebuah media sudah terunduh dan mengembalikan path lokalnya
    /// (null jika gagal). Dipanggil viewer saat berpindah item.
    /// </summary>
    public required Func<MediaPart, Task<string?>> EnsureDownloaded { get; init; }
}
