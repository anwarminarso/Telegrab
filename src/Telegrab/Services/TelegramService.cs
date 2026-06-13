using System.Globalization;
using System.IO;
using Telegrab.Models;
using TL;
using WTelegram;

namespace Telegrab.Services;

/// <summary>
/// Wrapper di atas WTelegramClient. Menangani koneksi, login bertahap,
/// dan operasi readonly (daftar chat, topik, pesan, download).
/// </summary>
public class TelegramService : IDisposable
{
    private readonly ConfigService _config;
    private Client? _client;

    // Jumlah unduhan file penuh yang sedang berjalan. Dipakai untuk memberi prioritas
    // ke unduhan dibanding permintaan thumbnail (lihat ThrottleThumbnailAsync).
    private int _activeDownloads;

    public TelegramService(ConfigService config) => _config = config;

    public Client Client => _client ?? throw new InvalidOperationException("Client is not initialized.");
    public User? Me => _client?.User;
    public bool IsLoggedIn => _client?.User != null;

    public void Init(AccountConfig account)
    {
        Dispose();
        _client = new Client(what => what switch
        {
            "api_id" => account.ApiId.ToString(),
            "api_hash" => account.ApiHash,
            "session_pathname" => _config.SessionPath,
            _ => null
        });
    }

    /// <summary>
    /// Satu langkah proses login. Kembalikan nilai yang diminta WTelegram berikutnya
    /// ("verification_code", "password", "name", dst). Mengembalikan null jika sudah login.
    /// </summary>
    public Task<string?> LoginAsync(string loginInfo) => Client.Login(loginInfo);

    /// <summary>Coba login otomatis memakai session tersimpan, tanpa OTP.</summary>
    public async Task<bool> TryAutoLoginAsync(AccountConfig account)
    {
        if (account.ApiId == 0 || string.IsNullOrWhiteSpace(account.ApiHash) || !_config.HasSession())
            return false;

        Init(account);
        try
        {
            await Client.LoginUserIfNeeded();
            return IsLoggedIn;
        }
        catch
        {
            Dispose();
            return false;
        }
    }

    /// <summary>Ambil semua chat/channel/grup yang diikuti user (readonly).</summary>
    public async Task<List<ChatItem>> GetChatsAsync()
    {
        var result = new List<ChatItem>();
        var chats = await Client.Messages_GetAllChats();

        foreach (var (_, chat) in chats.chats)
        {
            if (!chat.IsActive) continue;

            bool isForum = chat is Channel ch && ch.flags.HasFlag(Channel.Flags.forum);

            string kind = chat switch
            {
                Channel { IsChannel: true } => "Channel",
                Channel => "Supergroup",
                _ => "Group"
            };

            var item = new ChatItem
            {
                Id = chat.ID,
                Title = chat.Title,
                Kind = kind,
                Peer = chat.ToInputPeer(),
                PeerInfo = chat,
                IsForum = isForum
            };

            if (isForum)
                item.Topics.Add(new TopicItem { Title = "(loading topics...)", IsPlaceholder = true });

            result.Add(item);
        }

        return result.OrderBy(c => c.Title).ToList();
    }

    /// <summary>
    /// Menahan permintaan "ringan" (thumbnail) selama masih ada unduhan file penuh berjalan,
    /// agar bandwidth & kuota rate-limit Telegram diprioritaskan untuk unduhan. Permintaan
    /// thumbnail otomatis lanjut begitu antrian unduh idle, atau berhenti bila token dibatalkan.
    /// </summary>
    private async Task ThrottleThumbnailAsync(CancellationToken ct)
    {
        while (Volatile.Read(ref _activeDownloads) > 0)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(250, ct);
        }
    }

    /// <summary>Download foto profil chat (thumbnail kecil). Mengembalikan byte JPEG, atau null.</summary>
    public async Task<byte[]?> GetChatThumbnailAsync(IPeerInfo peer, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        try
        {
            await ThrottleThumbnailAsync(ct);
            await Client.DownloadProfilePhotoAsync(peer, ms, false, false);
        }
        catch
        {
            return null;
        }
        return ms.Length > 0 ? ms.ToArray() : null;
    }

    /// <summary>Ambil daftar topik (sub-chat) sebuah supergroup forum.</summary>
    public async Task<List<TopicItem>> GetTopicsAsync(InputPeer peer, long parentId, string parentTitle)
    {
        var result = new List<TopicItem>();
        var forums = await Client.Channels_GetAllForumTopics(peer);

        foreach (var t in forums.topics)
        {
            if (t is ForumTopic topic)
            {
                result.Add(new TopicItem
                {
                    Id = topic.id,
                    Title = topic.title,
                    ParentPeer = peer,
                    ParentId = parentId,
                    ParentTitle = parentTitle,
                    IconEmojiId = topic.icon_emoji_id,
                    IconColor = topic.icon_color
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Ambil thumbnail gambar untuk sekumpulan custom emoji (mis. ikon topik forum).
    /// Mengembalikan map: id custom emoji -> byte JPEG/PNG thumbnail.
    /// </summary>
    public async Task<Dictionary<long, byte[]>> GetCustomEmojiThumbnailsAsync(long[] emojiIds, CancellationToken ct = default)
    {
        var map = new Dictionary<long, byte[]>();
        if (emojiIds.Length == 0) return map;

        try
        {
            await ThrottleThumbnailAsync(ct);
            var docs = await Client.Messages_GetCustomEmojiDocuments(emojiIds);
            foreach (var docBase in docs)
            {
                if (docBase is not Document doc) continue;
                var thumb = PickThumb(doc.thumbs);
                if (thumb == null) continue;

                using var ms = new MemoryStream();
                await Client.DownloadFileAsync(doc, ms, thumb);
                if (ms.Length > 0)
                    map[doc.id] = ms.ToArray(); // id dokumen == id custom emoji
            }
        }
        catch
        {
            // abaikan; topik tetap pakai ikon default berwarna
        }

        return map;
    }

    /// <summary>
    /// Ambil riwayat pesan. Jika <paramref name="topicId"/> diisi, ambil pesan dari
    /// topik forum tersebut (Messages_GetReplies); jika null, seluruh chat (Messages_GetHistory).
    /// </summary>
    public async Task<MessagePage> GetMessagesAsync(InputPeer peer, int? topicId = null, int offsetId = 0, int limit = 50)
    {
        var history = topicId is int tid
            ? await Client.Messages_GetReplies(peer, tid, offset_id: offsetId, limit: limit)
            : await Client.Messages_GetHistory(peer, offset_id: offsetId, limit: limit);

        return BuildPage(history.Messages, offsetId);
    }

    /// <summary>Cari pesan berdasarkan teks (Messages_Search), opsional dibatasi ke satu topik.</summary>
    public async Task<MessagePage> SearchMessagesAsync(InputPeer peer, string query, int? topicId = null, int offsetId = 0, int limit = 50)
    {
        var result = await Client.Messages_Search(peer, query, offset_id: offsetId, limit: limit, top_msg_id: topicId);
        return BuildPage(result.Messages, offsetId);
    }

    /// <summary>Map pesan mentah jadi MessageItem, sekaligus menggabungkan album (grouped_id).</summary>
    private static MessagePage BuildPage(MessageBase[] raw, int offsetId)
    {
        var merged = new List<MessageItem>();
        int oldestId = offsetId;

        foreach (var msgBase in raw)
        {
            if (oldestId == offsetId || msgBase.ID < oldestId)
                oldestId = msgBase.ID;

            if (msgBase is not Message msg) continue;
            var item = MapMessage(msg);

            // Gabungkan pesan album yang berdekatan dengan grouped_id sama jadi satu item.
            if (item.GroupedId != 0 && merged.Count > 0 && merged[^1].GroupedId == item.GroupedId)
            {
                var prev = merged[^1];
                foreach (var m in item.Media)
                    prev.Media.Add(m);
                if (string.IsNullOrEmpty(prev.Text) && !string.IsNullOrEmpty(item.Text))
                    prev.Text = item.Text;
            }
            else
            {
                merged.Add(item);
            }
        }

        return new MessagePage(merged, raw.Length, oldestId);
    }

    private static MessageItem MapMessage(Message msg)
    {
        var item = new MessageItem
        {
            Id = msg.id,
            Text = msg.message ?? string.Empty,
            Date = msg.date,
            GroupedId = msg.grouped_id
        };

        var part = BuildMediaPart(msg.media);
        if (part != null)
        {
            part.MessageId = msg.id;
            part.MessageDate = msg.date;
            item.Media.Add(part);
        }

        return item;
    }

    private static MediaPart? BuildMediaPart(MessageMedia? media)
    {
        switch (media)
        {
            case MessageMediaPhoto { photo: Photo p }:
                return new MediaPart
                {
                    Kind = MediaKind.Photo,
                    Photo = p,
                    FileName = $"{p.id}.jpg"
                };

            case MessageMediaDocument { document: Document d }:
                var isVideo = d.mime_type?.StartsWith("video", StringComparison.OrdinalIgnoreCase) == true;
                return new MediaPart
                {
                    Kind = isVideo ? MediaKind.Video : MediaKind.File,
                    Document = d,
                    FileName = d.Filename ?? $"{d.id}.{ExtFromMime(d.mime_type)}",
                    FileSize = d.size
                };

            default:
                return null;
        }
    }

    /// <summary>Download thumbnail kecil dari sebuah media. Mengembalikan byte JPEG, atau null.</summary>
    public async Task<byte[]?> GetThumbnailAsync(MediaPart part, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        try
        {
            await ThrottleThumbnailAsync(ct);
            if (part.Photo is { } photo)
            {
                var thumb = PickThumb(photo.sizes);
                if (thumb == null) return null;
                await Client.DownloadFileAsync(photo, ms, thumb);
            }
            else if (part.Document is { } doc)
            {
                var thumb = PickThumb(doc.thumbs);
                if (thumb == null) return null;
                await Client.DownloadFileAsync(doc, ms, thumb);
            }
            else
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        return ms.Length > 0 ? ms.ToArray() : null;
    }

    /// <summary>
    /// Bangun path tujuan terorganisir: {root}/{Chat}/{Topik?}/{tanggal}_{idPesan}_{nama}.
    /// Folder dibuat bila belum ada.
    /// </summary>
    public string BuildTargetPath(string root, DownloadContext ctx, MediaPart part)
    {
        var folder = Path.Combine(root, SanitizeName(ctx.ChatTitle, "chat"));
        if (!string.IsNullOrWhiteSpace(ctx.TopicTitle))
            folder = Path.Combine(folder, SanitizeName(ctx.TopicTitle!, "topic"));

        Directory.CreateDirectory(folder);

        var datePrefix = part.MessageDate != default
            ? part.MessageDate.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "_"
            : string.Empty;
        // Sertakan MediaId agar media berbeda dalam satu pesan album (MessageId sama)
        // tidak saling menimpa meski nama file aslinya kebetulan sama.
        var name = $"{datePrefix}{part.MessageId}_{part.MediaId}_{SanitizeFileName(part.FileName)}";

        return Path.Combine(folder, name);
    }

    /// <summary>
    /// Download file media penuh ke <paramref name="destinationPath"/> dengan penanganan
    /// FLOOD_WAIT: bila Telegram meminta menunggu (rate limit), proses menunggu lalu mencoba lagi.
    /// </summary>
    public async Task<string> DownloadToPathAsync(
        MediaPart part,
        string destinationPath,
        IProgress<double>? progress = null,
        Action<int>? onFloodWait = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        void Report(long done, long total)
        {
            if (total > 0) progress?.Report((double)done / total);
        }

        Interlocked.Increment(ref _activeDownloads);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await using (var fs = File.Create(destinationPath))
                    {
                        if (part.Photo is { } photo)
                            await Client.DownloadFileAsync(photo, fs, (PhotoSizeBase?)null, Report);
                        else if (part.Document is { } doc)
                            await Client.DownloadFileAsync(doc, fs, (PhotoSizeBase?)null, Report);
                    }
                    return destinationPath;
                }
                catch (RpcException ex) when (TryGetFloodWait(ex, out var seconds))
                {
                    // Hapus file parsial sebelum menunggu & mengulang.
                    TryDelete(destinationPath);
                    onFloodWait?.Invoke(seconds);
                    await Task.Delay(TimeSpan.FromSeconds(seconds + 1), ct);
                }
                catch
                {
                    TryDelete(destinationPath);
                    throw;
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeDownloads);
        }
    }

    /// <summary>Deteksi error FLOOD_WAIT_x / FLOOD_PREMIUM_WAIT_x dan ambil detik tunggunya.</summary>
    private static bool TryGetFloodWait(RpcException ex, out int seconds)
    {
        seconds = 0;
        var msg = ex.Message ?? string.Empty;
        if (ex.Code != 420 && !msg.Contains("FLOOD", StringComparison.OrdinalIgnoreCase))
            return false;

        if (ex.X > 0) { seconds = ex.X; return true; }

        var idx = msg.LastIndexOf('_');
        if (idx >= 0 && int.TryParse(msg[(idx + 1)..], out var parsed))
        {
            seconds = parsed;
            return true;
        }
        return false;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* abaikan */ }
    }

    private static PhotoSizeBase? PickThumb(PhotoSizeBase[]? sizes)
    {
        if (sizes == null) return null;
        return sizes
            .Where(s => s is PhotoSize or PhotoSizeProgressive)
            .OrderBy(s => s switch
            {
                PhotoSize p => (long)p.w * p.h,
                PhotoSizeProgressive pp => (long)pp.w * pp.h,
                _ => long.MaxValue
            })
            .FirstOrDefault();
    }

    private static string ExtFromMime(string? mime)
    {
        if (string.IsNullOrEmpty(mime) || !mime.Contains('/')) return "bin";
        return mime[(mime.IndexOf('/') + 1)..];
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    /// <summary>Bersihkan teks jadi nama folder yang valid; fallback bila kosong.</summary>
    private static string SanitizeName(string name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.', ' ');
        return string.IsNullOrEmpty(name) ? fallback : name;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
        GC.SuppressFinalize(this);
    }
}
