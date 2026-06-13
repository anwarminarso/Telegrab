using Telegrab.Models;

namespace Telegrab.Services;

/// <summary>
/// Menyelesaikan <b>asosiasi deskripsi</b> (Requirement 7) untuk satu <see cref="MessageWindow"/>:
/// mengisi <c>Caption</c>/<c>CaptionSource</c>/<c>CaptionFromMessageId</c>/<c>Note</c>/
/// <c>NoteFromMessageId</c> pada tiap media.
///
/// Logika MURNI — TANPA dependensi MAUI/WTelegram — agar dapat dilink & diuji di proyek test
/// (net10.0). Sisi MAUI (task 8) meng-adaptasi <c>MessageItem</c>/<c>MediaPart</c> ke tipe
/// <see cref="ResolverMessage"/>/<see cref="ResolverMedia"/>.
///
/// Urutan resolusi per post (lihat design.md "Asosiasi deskripsi — algoritma"):
/// <list type="number">
///   <item>Identitas pengirim (<see cref="SenderMatches"/>).</item>
///   <item>Album: pesan ber-<c>group_id</c> sama menjadi satu post.</item>
///   <item>Caption langsung: <c>own</c> (post tunggal) atau <c>album</c>.</item>
///   <item>Reply (case 4/8/11): teks-murni yang membalas post &amp; pengirim sama → caption
///         (bila kosong) atau <c>note</c> (case 8, bila caption sudah ada).</item>
///   <item>Inferred (case 3/5/6/7): teks-murni terdekat dua arah, pengirim sama, tanpa media
///         penyela, dalam jendela waktu. Sebuah teks hanya menempel ke satu post terdekat.</item>
///   <item>None bila tidak ada kandidat.</item>
/// </list>
/// </summary>
public sealed class CaptionResolver
{
    /// <summary>
    /// Selesaikan asosiasi deskripsi untuk seluruh media di <paramref name="window"/>.
    /// </summary>
    public void Resolve(MessageWindow window, CaptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(window);
        options ??= CaptionOptions.Default;

        // Urutkan untuk logika adjacency: tanggal lalu id pesan.
        var ordered = window.Messages
            .OrderBy(m => m.DateUtc)
            .ThenBy(m => m.MessageId)
            .ToList();

        var posts = BuildPosts(ordered);
        if (posts.Count == 0)
            return; // tidak ada media → tidak ada yang perlu diasosiasikan (case 13: teks dibiarkan)

        // Peta id pesan → post (termasuk tiap anggota album) untuk resolusi target reply.
        var postByMessageId = new Dictionary<int, Post>();
        foreach (var post in posts)
            foreach (var msg in post.Messages)
                postByMessageId[msg.MessageId] = post;

        // (3) Caption langsung own/album.
        foreach (var post in posts)
            ApplyOwnOrAlbumCaption(post);

        // (4) Reply (case 4/8/11) — sekaligus catat teks yang sudah dikonsumsi.
        var consumedTextIds = new HashSet<int>();
        ApplyReplies(ordered, postByMessageId, consumedTextIds);

        // (5) Inferred (case 3/5/6/7).
        if (options.InferredEnabled)
            ApplyInferred(ordered, posts, options, consumedTextIds);

        // (6) Tulis hasil ke media (default tetap None bila tidak ada caption).
        foreach (var post in posts)
            post.WriteToMedia();
    }

    /// <summary>
    /// Apakah pengirim <paramref name="a"/> dan <paramref name="b"/> dianggap sama untuk
    /// asosiasi lintas pesan (Requirement 7.8).
    /// <list type="bullet">
    ///   <item>Jika <c>post_author</c> ada di keduanya → sama hanya bila identik (beda → tolak).</item>
    ///   <item>Jika keduanya punya <c>from_id</c> → sama bila identik.</item>
    ///   <item>Jika keduanya tanpa <c>from_id</c> (post channel murni) → dianggap sama.</item>
    ///   <item>Selainnya (salah satu punya <c>from_id</c>, lainnya tidak) → dianggap berbeda.</item>
    /// </list>
    /// </summary>
    public static bool SenderMatches(ISenderIdentity a, ISenderIdentity b)
    {
        // post_author menjadi penentu bila tersedia di kedua sisi (beda → tolak, sama → terima).
        if (!string.IsNullOrEmpty(a.PostAuthor) && !string.IsNullOrEmpty(b.PostAuthor))
            return string.Equals(a.PostAuthor, b.PostAuthor, StringComparison.Ordinal);

        var aHas = a.FromId.HasValue;
        var bHas = b.FromId.HasValue;

        if (aHas && bHas)
            return a.FromId!.Value == b.FromId!.Value;

        if (!aHas && !bHas)
            return true; // keduanya post channel murni → sama

        return false; // campuran: satu punya from_id, lainnya tidak → berbeda
    }

    // --- Langkah-langkah internal -----------------------------------------

    private static List<Post> BuildPosts(List<ResolverMessage> ordered)
    {
        var posts = new List<Post>();
        var albums = new Dictionary<long, Post>();

        foreach (var msg in ordered)
        {
            if (!msg.HasMedia)
                continue; // pesan teks murni / kosong bukan post

            var gid = msg.GroupId;
            if (gid is { } g && g != 0)
            {
                if (!albums.TryGetValue(g, out var album))
                {
                    album = new Post { GroupId = g };
                    albums[g] = album;
                    posts.Add(album);
                }
                album.Messages.Add(msg);
            }
            else
            {
                var post = new Post { GroupId = null };
                post.Messages.Add(msg);
                posts.Add(post);
            }
        }

        return posts;
    }

    private static void ApplyOwnOrAlbumCaption(Post post)
    {
        // Caption = teks non-kosong dari anggota mana pun (utamakan anggota paling awal).
        var captionMsg = post.Messages.FirstOrDefault(m => m.HasText);
        if (captionMsg is null)
            return;

        post.Caption = captionMsg.Text;
        post.Source = post.IsAlbum ? CaptionSource.Album : CaptionSource.Own;
        post.CaptionFromMessageId = captionMsg.MessageId;
    }

    private static void ApplyReplies(
        List<ResolverMessage> ordered,
        Dictionary<int, Post> postByMessageId,
        HashSet<int> consumedTextIds)
    {
        foreach (var msg in ordered)
        {
            if (!msg.IsPureText)
                continue; // sumber reply harus teks murni
            if (msg.ReplyToMsgId is not int targetId)
                continue;
            if (!postByMessageId.TryGetValue(targetId, out var post))
                continue; // target tidak termuat di window (case 12 best-effort)
            if (!SenderMatches(msg, post))
                continue; // case 4: penulis balasan berbeda → tidak ditempelkan

            if (!post.HasCaption)
            {
                // Post belum punya caption → teks reply jadi caption.
                post.Caption = msg.Text;
                post.Source = CaptionSource.Reply;
                post.CaptionFromMessageId = msg.MessageId;
                consumedTextIds.Add(msg.MessageId);
            }
            else if (post.Note is null)
            {
                // case 8: caption utama sudah ada → reply disimpan sebagai catatan terpisah.
                post.Note = msg.Text;
                post.NoteFromMessageId = msg.MessageId;
                consumedTextIds.Add(msg.MessageId);
            }
        }
    }

    private static void ApplyInferred(
        List<ResolverMessage> ordered,
        List<Post> posts,
        CaptionOptions options,
        HashSet<int> consumedTextIds)
    {
        var targets = posts.Where(p => !p.HasCaption).ToList();
        if (targets.Count == 0)
            return;

        var indexOf = new Dictionary<ResolverMessage, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < ordered.Count; i++)
            indexOf[ordered[i]] = i;

        // Kumpulkan kandidat terbaik per post.
        var candidates = new List<(Post Post, ResolverMessage Text, double Delta)>();

        foreach (var post in targets)
        {
            int minIdx = post.Messages.Min(m => indexOf[m]);
            int maxIdx = post.Messages.Max(m => indexOf[m]);

            ResolverMessage? best = null;
            double bestDelta = double.MaxValue;

            void Consider(ResolverMessage m)
            {
                if (consumedTextIds.Contains(m.MessageId))
                    return;
                if (!SenderMatches(m, post))
                    return;
                double delta = post.MinDeltaSeconds(m.DateUtc);
                if (delta <= options.InferredWindowSeconds && delta < bestDelta)
                {
                    best = m;
                    bestDelta = delta;
                }
            }

            // Mundur (teks di atas, case 5): berhenti bila menemui media penyela.
            for (int i = minIdx - 1; i >= 0; i--)
            {
                var m = ordered[i];
                if (post.Contains(m)) continue;
                if (m.HasMedia) break;       // media lain di antara → terblokir
                if (m.IsPureText) Consider(m);
            }

            // Maju (teks di bawah, case 3/6): berhenti bila menemui media penyela.
            for (int i = maxIdx + 1; i < ordered.Count; i++)
            {
                var m = ordered[i];
                if (post.Contains(m)) continue;
                if (m.HasMedia) break;
                if (m.IsPureText) Consider(m);
            }

            if (best is not null)
                candidates.Add((post, best, bestDelta));
        }

        // Resolusi konflik (case 7): satu teks hanya menempel ke post TERDEKAT.
        foreach (var grp in candidates.GroupBy(c => c.Text, ReferenceEqualityComparer.Instance))
        {
            var winner = grp
                .OrderBy(c => c.Delta)
                .ThenBy(c => c.Post.FirstMessageId)
                .First();

            var post = winner.Post;
            if (post.HasCaption)
                continue; // jaga-jaga

            post.Caption = winner.Text.Text;
            post.Source = CaptionSource.Inferred;
            post.CaptionFromMessageId = winner.Text.MessageId;
        }
    }

    /// <summary>
    /// Post logis = satu pesan media tunggal ATAU satu album (anggota ber-<c>group_id</c> sama).
    /// </summary>
    private sealed class Post : ISenderIdentity
    {
        public long? GroupId { get; init; }
        public List<ResolverMessage> Messages { get; } = new();

        public bool IsAlbum => GroupId is { } g && g != 0;

        // Identitas pengirim representatif (anggota album berbagi pengirim).
        public long? FromId => Messages[0].FromId;
        public string? PostAuthor => Messages[0].PostAuthor;

        public int FirstMessageId => Messages.Min(m => m.MessageId);

        public string? Caption { get; set; }
        public CaptionSource Source { get; set; } = CaptionSource.None;
        public int? CaptionFromMessageId { get; set; }
        public string? Note { get; set; }
        public int? NoteFromMessageId { get; set; }

        public bool HasCaption =>
            Source != CaptionSource.None && !string.IsNullOrWhiteSpace(Caption);

        public bool Contains(ResolverMessage m) => Messages.Contains(m);

        /// <summary>Selisih waktu (detik) terkecil antara <paramref name="t"/> dan anggota post.</summary>
        public double MinDeltaSeconds(DateTime t) =>
            Messages.Min(m => Math.Abs((m.DateUtc - t).TotalSeconds));

        public void WriteToMedia()
        {
            foreach (var msg in Messages)
            foreach (var media in msg.Media)
            {
                media.Caption = Caption;
                media.CaptionSource = Source;
                media.CaptionFromMessageId = CaptionFromMessageId;
                media.Note = Note;
                media.NoteFromMessageId = NoteFromMessageId;
            }
        }
    }
}
