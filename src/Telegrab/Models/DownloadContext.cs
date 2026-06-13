namespace Telegrab.Models;

/// <summary>
/// Konteks asal sebuah media saat diunduh. Dipakai untuk menata folder/nama file
/// dan membentuk kunci manifest "sudah diunduh".
/// </summary>
/// <param name="ChatId">
/// Id chat/channel/grup sumber. Untuk topik forum ini adalah id supergroup INDUK (bukan id topik),
/// konsisten dengan kolom <c>chat_id</c> pada manifest — pembedaan topik dilakukan lewat
/// <paramref name="TopicTitle"/> (subfolder), bukan lewat <c>ChatId</c>.
/// </param>
/// <param name="ChatTitle">Judul chat (dipakai sebagai nama subfolder).</param>
/// <param name="TopicTitle">Judul topik forum, bila ada (subfolder kedua).</param>
public record DownloadContext(long ChatId, string ChatTitle, string? TopicTitle);
