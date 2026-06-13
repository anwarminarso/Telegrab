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

    // Resolver asosiasi deskripsi (logika murni). Dipanggil pada level halaman setelah
    // pesan dipetakan & album digabung (lihat ResolvePageCaptionsAsync).
    private readonly CaptionResolver _captionResolver = new();

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

        var page = BuildPage(history.Messages, offsetId);
        return await ResolvePageCaptionsAsync(page, peer);
    }

    /// <summary>Cari pesan berdasarkan teks (Messages_Search), opsional dibatasi ke satu topik.</summary>
    public async Task<MessagePage> SearchMessagesAsync(InputPeer peer, string query, int? topicId = null, int offsetId = 0, int limit = 50)
    {
        var result = await Client.Messages_Search(peer, query, offset_id: offsetId, limit: limit, top_msg_id: topicId);
        var page = BuildPage(result.Messages, offsetId);
        return await ResolvePageCaptionsAsync(page, peer);
    }

    /// <summary>
    /// Fetch tambahan (case 12): ambil pesan berdasarkan id-nya untuk menyelesaikan target
    /// reply yang tidak termuat di halaman saat ini. Membungkus <c>Messages_GetMessages</c>
    /// (atau <c>Channels_GetMessages</c> bila peer adalah channel/supergroup, karena id pesan
    /// channel hanya valid lewat varian itu). Mengembalikan hanya entri <see cref="Message"/>.
    /// </summary>
    public async Task<IReadOnlyList<Message>> FetchMessagesByIdAsync(InputPeer peer, int[] ids)
    {
        if (ids is null || ids.Length == 0)
            return Array.Empty<Message>();

        var inputs = ids.Select(id => (InputMessage)new InputMessageID { id = id }).ToArray();

        Messages_MessagesBase result = peer is InputPeerChannel ch
            ? await Client.Channels_GetMessages(new InputChannel(ch.channel_id, ch.access_hash), inputs)
            : await Client.Messages_GetMessages(inputs);

        return result.Messages.OfType<Message>().ToList();
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

    /// <summary>
    /// Integrasi <see cref="CaptionResolver"/> pada level halaman (Requirement 7.5, 7.9).
    /// Meng-adaptasi <see cref="MessageItem"/>/<see cref="MediaPart"/> halaman menjadi window
    /// pure-DTO (<see cref="ResolverMessage"/>/<see cref="ResolverMedia"/>), menyelesaikan reply
    /// lintas-halaman (case 12) lewat fetch tambahan best-effort, menjalankan resolusi, lalu
    /// menyalin hasil (Caption/CaptionSource/CaptionFromMessageId/Note/NoteFromMessageId) kembali
    /// ke <see cref="MediaPart"/> berdasarkan <see cref="MediaPart.MediaId"/>.
    /// </summary>
    private async Task<MessagePage> ResolvePageCaptionsAsync(MessagePage page, InputPeer peer)
    {
        if (page.Items.Count == 0)
            return page;

        var window = new MessageWindow();
        // Peta MediaId → MediaPart HALAMAN (target penyalinan hasil). Hanya media halaman yang
        // ikut; media hasil fetch tambahan tidak ditulis balik (tak tampil/diunduh di halaman ini).
        var partByMediaId = new Dictionary<long, MediaPart>();
        // Id pesan yang sudah ada di window (untuk mendeteksi target reply yang "hilang").
        var presentIds = new HashSet<int>();

        foreach (var item in page.Items)
        {
            AdaptItem(item, window, partByMediaId);
            presentIds.Add(item.Id);
            foreach (var part in item.Media)
                if (part.MessageId != 0)
                    presentIds.Add(part.MessageId);
        }

        // Case 12: reply yang target (reply_to_msg_id)-nya tidak termuat di window → fetch tambahan
        // lalu masukkan ke window sebelum resolusi. Best-effort: kegagalan diabaikan.
        var missingTargets = window.Messages
            .Where(m => m.IsPureText && m.ReplyToMsgId is int t && !presentIds.Contains(t))
            .Select(m => m.ReplyToMsgId!.Value)
            .Distinct()
            .ToArray();

        if (missingTargets.Length > 0)
        {
            try
            {
                var fetched = await FetchMessagesByIdAsync(peer, missingTargets);
                foreach (var msg in fetched)
                {
                    var extra = MapMessage(msg);
                    // mediaMap = null: media hasil fetch tidak ditulis balik ke halaman.
                    AdaptItem(extra, window, mediaMap: null);
                    presentIds.Add(extra.Id);
                }
            }
            catch
            {
                // best-effort: resolver lanjut tanpa data tambahan (turun ke inferred/none).
            }
        }

        _captionResolver.Resolve(window, CaptionOptions.Default);

        // Salin hasil resolver ke MediaPart halaman berdasarkan MediaId.
        foreach (var msg in window.Messages)
        {
            foreach (var media in msg.Media)
            {
                if (!partByMediaId.TryGetValue(media.MediaId, out var part))
                    continue;
                part.Caption = media.Caption;
                part.CaptionSource = media.CaptionSource;
                part.CaptionFromMessageId = media.CaptionFromMessageId;
                part.Note = media.Note;
                part.NoteFromMessageId = media.NoteFromMessageId;
            }
        }

        return page;
    }

    /// <summary>
    /// Adaptasi satu <see cref="MessageItem"/> menjadi satu/lebih <see cref="ResolverMessage"/>
    /// dan menambahkannya ke <paramref name="window"/>.
    /// <list type="bullet">
    ///   <item>Pesan teks-murni (tanpa media) → satu <see cref="ResolverMessage"/> tanpa media,
    ///         agar tersedia sebagai sumber reply/inferred (case 3/5/6/13).</item>
    ///   <item>Pesan bermedia (termasuk album yang sudah digabung di <see cref="BuildPage"/>) →
    ///         satu <see cref="ResolverMessage"/> PER <see cref="MediaPart"/>, memakai
    ///         <see cref="MediaPart.MessageId"/> asli tiap anggota agar reply ke anggota album
    ///         mana pun tetap dikenali (case 11). Caption dilekatkan pada anggota pertama.</item>
    /// </list>
    /// Bila <paramref name="mediaMap"/> non-null, tiap media dipetakan (MediaId → MediaPart)
    /// untuk penyalinan hasil balik.
    /// </summary>
    private static void AdaptItem(MessageItem item, MessageWindow window, Dictionary<long, MediaPart>? mediaMap)
    {
        long? groupId = item.GroupedId != 0 ? item.GroupedId : null;

        if (!item.HasMedia)
        {
            window.Messages.Add(new ResolverMessage
            {
                MessageId = item.Id,
                DateUtc = ToUtc(item.Date),
                Text = item.Text,
                GroupId = groupId,
                ReplyToMsgId = item.ReplyToMsgId,
                FromId = item.FromId,
                PostAuthor = item.PostAuthor
            });
            return;
        }

        bool textAssigned = false;
        foreach (var part in item.Media)
        {
            var rmsg = new ResolverMessage
            {
                MessageId = part.MessageId != 0 ? part.MessageId : item.Id,
                DateUtc = ToUtc(part.MessageDate != default ? part.MessageDate : item.Date),
                // Caption album/own hanya pada anggota pertama; sisanya tanpa teks.
                Text = textAssigned ? null : item.Text,
                GroupId = groupId,
                ReplyToMsgId = part.ReplyToMsgId ?? item.ReplyToMsgId,
                FromId = part.FromId ?? item.FromId,
                PostAuthor = part.PostAuthor ?? item.PostAuthor
            };
            rmsg.Media.Add(new ResolverMedia { MediaId = part.MediaId });
            window.Messages.Add(rmsg);
            textAssigned = true;

            if (mediaMap != null)
                mediaMap[part.MediaId] = part;
        }
    }

    /// <summary>
    /// Normalisasi tanggal ke UTC untuk resolver. Tanggal TL (WTelegram) sudah UTC; defensif
    /// terhadap <see cref="DateTimeKind"/> lain (Local → konversi, Unspecified → anggap UTC).
    /// </summary>
    private static DateTime ToUtc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Local => dt.ToUniversalTime(),
        _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
    };

    private static MessageItem MapMessage(Message msg)
    {
        // reply_to_msg_id (case 4/8/11/12); 0 berarti tidak membalas apa pun.
        int? replyToMsgId = null;
        if (msg.reply_to is MessageReplyHeader rh && rh.reply_to_msg_id != 0)
            replyToMsgId = rh.reply_to_msg_id;

        var fromId = ExtractPeerId(msg.from_id);
        var postAuthor = string.IsNullOrWhiteSpace(msg.post_author) ? null : msg.post_author;
        // Sender best-effort dari Message saja: pakai post_author bila ada. Resolver hanya
        // butuh from_id/post_author untuk pencocokan pengirim; nama tampilan tak wajib.
        var sender = postAuthor;

        var item = new MessageItem
        {
            Id = msg.id,
            Text = msg.message ?? string.Empty,
            Date = msg.date,
            GroupedId = msg.grouped_id,
            ReplyToMsgId = replyToMsgId,
            FromId = fromId,
            PostAuthor = postAuthor
        };

        var part = BuildMediaPart(msg.media);
        if (part != null)
        {
            part.MessageId = msg.id;
            part.MessageDate = msg.date;
            part.ReplyToMsgId = replyToMsgId;
            part.FromId = fromId;
            part.PostAuthor = postAuthor;
            part.Sender = sender;
            item.Media.Add(part);
        }

        return item;
    }

    /// <summary>Ambil id numerik peer dari <see cref="Peer"/> (user/chat/channel); null bila tak ada.</summary>
    private static long? ExtractPeerId(Peer? peer) => peer switch
    {
        PeerUser u => u.user_id,
        PeerChannel c => c.channel_id,
        PeerChat ch => ch.chat_id,
        _ => null
    };

    private static MediaPart? BuildMediaPart(MessageMedia? media)
    {
        switch (media)
        {
            case MessageMediaPhoto { photo: Photo p }:
                var (pw, ph) = GetPhotoDimensions(p);
                return new MediaPart
                {
                    Kind = MediaKind.Photo,
                    Photo = p,
                    FileName = $"{p.id}.jpg",
                    FileSize = LargestPhotoSizeBytes(p),
                    Width = pw,
                    Height = ph
                };

            case MessageMediaDocument { document: Document d }:
                var isVideo = d.mime_type?.StartsWith("video", StringComparison.OrdinalIgnoreCase) == true;
                var part = new MediaPart
                {
                    Kind = isVideo ? MediaKind.Video : MediaKind.File,
                    Document = d,
                    FileName = d.Filename ?? $"{d.id}.{ExtFromMime(d.mime_type)}",
                    FileSize = d.size
                };

                // Dimensi/durasi dari atribut dokumen (video atau gambar yang dikirim sbg file).
                foreach (var attr in d.attributes ?? Array.Empty<DocumentAttribute>())
                {
                    switch (attr)
                    {
                        case DocumentAttributeVideo v:
                            part.DurationSeconds = v.duration;
                            part.Width = v.w;
                            part.Height = v.h;
                            break;
                        case DocumentAttributeImageSize img:
                            part.Width = img.w;
                            part.Height = img.h;
                            break;
                    }
                }

                return part;

            default:
                return null;
        }
    }

    /// <summary>Dimensi foto: ambil w/h dari PhotoSize terbesar (berdasarkan luas).</summary>
    private static (int? w, int? h) GetPhotoDimensions(Photo photo)
    {
        int? w = null, h = null;
        long best = -1;
        foreach (var s in photo.sizes ?? Array.Empty<PhotoSizeBase>())
        {
            (int sw, int sh) = s switch
            {
                PhotoSize ps => (ps.w, ps.h),
                PhotoSizeProgressive pp => (pp.w, pp.h),
                _ => (0, 0)
            };
            long area = (long)sw * sh;
            if (area > best) { best = area; w = sw; h = sh; }
        }
        return (w, h);
    }

    /// <summary>Perkiraan ukuran byte foto: ambil PhotoSize terbesar yang punya info ukuran.</summary>
    private static long LargestPhotoSizeBytes(Photo photo)
    {
        long max = 0;
        foreach (var s in photo.sizes ?? Array.Empty<PhotoSizeBase>())
        {
            long sz = s switch
            {
                PhotoSize ps => ps.size,
                PhotoSizeProgressive pp => pp.sizes is { Length: > 0 } ? pp.sizes[^1] : 0,
                _ => 0
            };
            if (sz > max) max = sz;
        }
        return max;
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
    /// Bentuk folder RELATIF (terhadap root) untuk sebuah konteks unduhan, konsisten dengan
    /// <see cref="BuildTargetPath"/>: <c>{Chat}/{Topik?}</c>. Memakai sanitasi yang sama agar
    /// folder cocok dengan tempat file ditulis, lalu dinormalkan ke pemisah '/' untuk dipakai
    /// oleh <see cref="DocumentationService"/> (mis. aksi Rebuild documentation).
    /// </summary>
    public string BuildRelativeFolder(DownloadContext ctx)
    {
        var folder = SanitizeName(ctx.ChatTitle, "chat");
        if (!string.IsNullOrWhiteSpace(ctx.TopicTitle))
            folder = folder + "/" + SanitizeName(ctx.TopicTitle!, "topic");
        return folder;
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
