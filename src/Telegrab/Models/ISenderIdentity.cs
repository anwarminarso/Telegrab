namespace Telegrab.Models;

/// <summary>
/// Identitas pengirim minimal yang dibutuhkan untuk pembandingan asosiasi lintas pesan
/// (<c>CaptionResolver.SenderMatches</c>, Requirement 7.8). Tipe logika MURNI (tanpa MAUI).
/// </summary>
public interface ISenderIdentity
{
    /// <summary>Id pengirim (peer); null untuk post channel murni.</summary>
    long? FromId { get; }

    /// <summary>Nama penulis post (channel) bila ada.</summary>
    string? PostAuthor { get; }
}
