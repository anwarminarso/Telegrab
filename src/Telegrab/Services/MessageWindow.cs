using Telegrab.Models;

namespace Telegrab.Services;

/// <summary>
/// Sebuah "window" pesan yang diproses bersama oleh <see cref="CaptionResolver"/>: satu halaman
/// pesan ditambah pesan tambahan hasil fetch (case 12). Tipe logika MURNI (tanpa MAUI) agar
/// dapat dilink ke proyek test.
///
/// Urutan elemen di <see cref="Messages"/> tidak harus terurut; resolver mengurutkannya secara
/// internal berdasarkan (tanggal, id pesan) untuk logika adjacency.
/// </summary>
public sealed class MessageWindow
{
    /// <summary>Pesan-pesan dalam window.</summary>
    public List<ResolverMessage> Messages { get; } = new();

    public MessageWindow() { }

    public MessageWindow(IEnumerable<ResolverMessage> messages)
    {
        Messages.AddRange(messages);
    }
}
