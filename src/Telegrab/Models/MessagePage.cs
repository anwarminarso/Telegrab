namespace Telegrab.Models;

/// <summary>
/// Hasil satu halaman pesan.
/// <para><see cref="Items"/> = pesan yang bisa ditampilkan (sudah difilter).</para>
/// <para><see cref="RawCount"/> = jumlah mentah dari Telegram (untuk menentukan masih ada halaman).</para>
/// <para><see cref="OldestId"/> = id pesan tertua pada halaman ini (offset halaman berikutnya).</para>
/// </summary>
public record MessagePage(List<MessageItem> Items, int RawCount, int OldestId);
