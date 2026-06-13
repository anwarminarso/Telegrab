namespace Telegrab.Models;

/// <summary>
/// Permintaan untuk membuka penampil dokumentasi (<c>README.md</c>) sebuah chat/topik
/// (Fase 2, Requirement 10). Membawa folder relatif (terhadap root) dan root absolut aktif
/// agar penampil dapat me-resolve lokasi <c>README.md</c> dan tautan media relatif di dalamnya.
/// </summary>
/// <param name="RelativeFolder">Folder relatif <c>{Chat}/{Topik?}</c> terhadap root.</param>
/// <param name="Root">Root download absolut aktif.</param>
public sealed record DocumentationRequest(string RelativeFolder, string Root);
