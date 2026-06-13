using Telegrab.Models;
using Telegrab.Services;

namespace Telegrab.Tests;

/// <summary>
/// Unit test untuk <see cref="CaptionResolver"/> (task 7.1) — menguji tiap case Lampiran A (1–13)
/// dengan window sintetis, plus kebijakan pengirim channel (<c>SenderMatches</c>) dan batas
/// jendela <c>inferred</c>.
///
/// Memvalidasi:
///  - Property 5 (asosiasi lintas pesan butuh pengirim sama): case 4 beda pengirim, SenderMatches.
///  - Property 6 (transparansi sumber): tiap caption membawa CaptionSource yang sesuai; none bila kosong.
///  - Property 9 (album sebagai satu post): caption album berlaku ke seluruh anggota.
/// </summary>
public sealed class CaptionResolverTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ResolverMessage Text(
        int id, double offsetSeconds, string text,
        long? fromId = 1, string? postAuthor = null, int? replyTo = null) => new()
    {
        MessageId = id,
        DateUtc = T0.AddSeconds(offsetSeconds),
        Text = text,
        FromId = fromId,
        PostAuthor = postAuthor,
        ReplyToMsgId = replyTo,
    };

    private static ResolverMessage Media(
        int id, double offsetSeconds, string? caption = null, long? groupId = null,
        long? fromId = 1, string? postAuthor = null, int mediaCount = 1, int? replyTo = null)
    {
        var m = new ResolverMessage
        {
            MessageId = id,
            DateUtc = T0.AddSeconds(offsetSeconds),
            Text = caption,
            GroupId = groupId,
            FromId = fromId,
            PostAuthor = postAuthor,
            ReplyToMsgId = replyTo,
        };
        for (int i = 0; i < mediaCount; i++)
            m.Media.Add(new ResolverMedia { MediaId = id * 100L + i });
        return m;
    }

    private static void Resolve(params ResolverMessage[] messages) =>
        new CaptionResolver().Resolve(new MessageWindow(messages), CaptionOptions.Default);

    private static ResolverMedia First(ResolverMessage m) => m.Media[0];

    // ============================ Case 1 ============================
    [Fact]
    public void Case1_FileWithOwnCaption_UsesOwn()
    {
        var media = Media(1, 0, caption: "hello world");
        Resolve(media);

        var r = First(media);
        Assert.Equal(CaptionSource.Own, r.CaptionSource);
        Assert.Equal("hello world", r.Caption);
        Assert.Equal(1, r.CaptionFromMessageId);
        Assert.Null(r.Note);
    }

    // ============================ Case 2 ============================
    [Fact]
    public void Case2_AlbumWithCaptionOnOneMember_AppliesAlbumToAll()
    {
        // Album: 3 anggota berbagi group_id; hanya anggota pertama yang punya teks.
        var a = Media(10, 0, caption: "album caption", groupId: 555);
        var b = Media(11, 0, caption: null, groupId: 555);
        var c = Media(12, 0, caption: null, groupId: 555);
        Resolve(a, b, c);

        foreach (var msg in new[] { a, b, c })
        {
            var r = First(msg);
            Assert.Equal(CaptionSource.Album, r.CaptionSource);
            Assert.Equal("album caption", r.Caption);
            Assert.Equal(10, r.CaptionFromMessageId);
        }
    }

    // ============================ Case 3 ============================
    [Fact]
    public void Case3_FileThenStandaloneText_SameSenderWithinWindow_Inferred()
    {
        var media = Media(1, 0);                       // tanpa caption
        var text = Text(2, 20, "deskripsi menyusul");  // teks berdiri sendiri, +20s
        Resolve(media, text);

        var r = First(media);
        Assert.Equal(CaptionSource.Inferred, r.CaptionSource);
        Assert.Equal("deskripsi menyusul", r.Caption);
        Assert.Equal(2, r.CaptionFromMessageId);
    }

    // ============================ Case 4 ============================
    [Fact]
    public void Case4_ReplySameSender_UsesReply()
    {
        var media = Media(1, 0);
        var reply = Text(2, 5, "ini balasan deskripsi", replyTo: 1);
        Resolve(media, reply);

        var r = First(media);
        Assert.Equal(CaptionSource.Reply, r.CaptionSource);
        Assert.Equal("ini balasan deskripsi", r.Caption);
        Assert.Equal(2, r.CaptionFromMessageId);
    }

    [Fact]
    public void Case4_ReplyDifferentSender_NotAttached()
    {
        // Property 5: asosiasi lintas pesan ditolak bila pengirim berbeda.
        var media = Media(1, 0, fromId: 1);
        var reply = Text(2, 5, "balasan orang lain", fromId: 2, replyTo: 1);
        Resolve(media, reply);

        var r = First(media);
        Assert.Equal(CaptionSource.None, r.CaptionSource);
        Assert.Null(r.Caption);
    }

    // ============================ Case 5 ============================
    [Fact]
    public void Case5_TextThenFile_NoReply_InfersTextAbove()
    {
        var text = Text(1, 0, "teks di atas media");
        var media = Media(2, 15);
        Resolve(text, media);

        var r = First(media);
        Assert.Equal(CaptionSource.Inferred, r.CaptionSource);
        Assert.Equal("teks di atas media", r.Caption);
        Assert.Equal(1, r.CaptionFromMessageId);
    }

    // ============================ Case 6 ============================
    [Fact]
    public void Case6_FileThenText_NoReply_Inferred()
    {
        var media = Media(1, 0, groupId: 77);
        var member2 = Media(2, 0, groupId: 77);
        var text = Text(3, 10, "keterangan album");
        Resolve(media, member2, text);

        // Album tanpa caption sendiri → inferred dari teks berdekatan, berlaku ke semua anggota.
        foreach (var msg in new[] { media, member2 })
        {
            var r = First(msg);
            Assert.Equal(CaptionSource.Inferred, r.CaptionSource);
            Assert.Equal("keterangan album", r.Caption);
        }
    }

    // ============================ Case 7 ============================
    [Fact]
    public void Case7_OneTextForMultipleMediaPosts_OnlyNearestAttaches()
    {
        // P1 di T0, teks di +5s (dekat P1), P2 di +40s. Teks hanya menempel ke P1 (terdekat);
        // P2 terblokir media P1 saat mundur → none.
        var p1 = Media(1, 0);
        var text = Text(2, 5, "satu teks");
        var p2 = Media(3, 40);
        Resolve(p1, text, p2);

        var r1 = First(p1);
        var r2 = First(p2);
        Assert.Equal(CaptionSource.Inferred, r1.CaptionSource);
        Assert.Equal("satu teks", r1.Caption);
        Assert.Equal(CaptionSource.None, r2.CaptionSource);
        Assert.Null(r2.Caption);
    }

    // ============================ Case 8 ============================
    [Fact]
    public void Case8_OwnCaptionPlusReplySameSender_ReplyStoredAsNote()
    {
        var media = Media(1, 0, caption: "caption utama");
        var reply = Text(2, 5, "tambahan info", replyTo: 1);
        Resolve(media, reply);

        var r = First(media);
        Assert.Equal(CaptionSource.Own, r.CaptionSource);
        Assert.Equal("caption utama", r.Caption);
        Assert.Equal("tambahan info", r.Note);
        Assert.Equal(2, r.NoteFromMessageId);
    }

    [Fact]
    public void Case8_EmptyMainCaptionWithReply_ReplyBecomesCaption()
    {
        // Caption utama kosong + reply qualifying → reply menjadi caption (source=reply), bukan note.
        var media = Media(1, 0, caption: null);
        var reply = Text(2, 5, "jadi caption", replyTo: 1);
        Resolve(media, reply);

        var r = First(media);
        Assert.Equal(CaptionSource.Reply, r.CaptionSource);
        Assert.Equal("jadi caption", r.Caption);
        Assert.Null(r.Note);
    }

    // ============================ Case 9 ============================
    [Fact]
    public void Case9_ForwardedMediaWithCaption_LikeOwn()
    {
        // Media forward membawa caption pada pesan yang sama → own.
        var media = Media(1, 0, caption: "caption ikut forward");
        Resolve(media);

        var r = First(media);
        Assert.Equal(CaptionSource.Own, r.CaptionSource);
        Assert.Equal("caption ikut forward", r.Caption);
    }

    // ============================ Case 10 ============================
    [Fact]
    public void Case10_MediaWithNoDescriptionAnywhere_None()
    {
        var media = Media(1, 0);
        Resolve(media);

        var r = First(media);
        Assert.Equal(CaptionSource.None, r.CaptionSource);
        Assert.Null(r.Caption);
        Assert.Null(r.Note);
    }

    // ============================ Case 11 ============================
    [Fact]
    public void Case11_ReplyToOneAlbumMember_AppliesToWholeAlbum()
    {
        var a = Media(10, 0, groupId: 999);
        var b = Media(11, 0, groupId: 999);
        var c = Media(12, 0, groupId: 999);
        var reply = Text(20, 5, "deskripsi untuk album", replyTo: 11); // menunjuk anggota ke-2
        Resolve(a, b, c, reply);

        foreach (var msg in new[] { a, b, c })
        {
            var r = First(msg);
            Assert.Equal(CaptionSource.Reply, r.CaptionSource);
            Assert.Equal("deskripsi untuk album", r.Caption);
            Assert.Equal(20, r.CaptionFromMessageId);
        }
    }

    // ============================ Case 12 ============================
    [Fact]
    public void Case12_ReplyTargetInjectedIntoWindow_Resolves()
    {
        // Target reply (id 1) berada di luar halaman semula, lalu disuntikkan ke window
        // (hasil fetch tambahan). Resolver memperlakukannya seperti reply biasa.
        var injectedTarget = Media(1, 0);
        var replyOnLaterPage = Text(99, 8, "deskripsi lintas halaman", replyTo: 1);
        Resolve(injectedTarget, replyOnLaterPage);

        var r = First(injectedTarget);
        Assert.Equal(CaptionSource.Reply, r.CaptionSource);
        Assert.Equal("deskripsi lintas halaman", r.Caption);
    }

    [Fact]
    public void Case12_ReplyTargetMissingFromWindow_NotAttachedAsReply()
    {
        // Bila target tidak ada di window (belum di-fetch) → asosiasi reply eksplisit TIDAK terjadi.
        // Best-effort (design.md): resolver "turun ke inferred/none". Di sini teks reply berada di
        // luar jendela inferred (>60s) sehingga juga tidak ter-infer → hasil akhir None.
        var media = Media(1, 0);
        var reply = Text(99, 600, "balasan ke pesan tak termuat", replyTo: 12345);
        Resolve(media, reply);

        var r = First(media);
        Assert.NotEqual(CaptionSource.Reply, r.CaptionSource);
        Assert.Equal(CaptionSource.None, r.CaptionSource);
    }

    [Fact]
    public void Case12_OrphanReplyWithinWindow_FallsBackToInferred()
    {
        // Best-effort: reply yang target-nya tak termuat, namun teksnya berdekatan & pengirim sama,
        // turun menjadi kandidat inferred (bukan reply). Mendokumentasikan perilaku fallback.
        var media = Media(1, 0);
        var reply = Text(99, 8, "balasan target hilang", replyTo: 12345);
        Resolve(media, reply);

        var r = First(media);
        Assert.Equal(CaptionSource.Inferred, r.CaptionSource);
    }

    // ============================ Case 13 ============================
    [Fact]
    public void Case13_StandaloneTextNotADescription_NotAttached()
    {
        // Teks berdiri sendiri di luar jendela waktu (>60s) → bukan deskripsi media apa pun.
        var media = Media(1, 0);
        var text = Text(2, 600, "obrolan tak terkait");
        Resolve(media, text);

        var r = First(media);
        Assert.Equal(CaptionSource.None, r.CaptionSource);
        Assert.Null(r.Caption);
    }

    // ===================== Kebijakan pengirim (SenderMatches) =====================
    [Fact]
    public void SenderMatches_BothFromIdEqual_True() =>
        Assert.True(CaptionResolver.SenderMatches(Text(1, 0, "a", fromId: 5), Text(2, 0, "b", fromId: 5)));

    [Fact]
    public void SenderMatches_BothFromIdDiffer_False() =>
        Assert.False(CaptionResolver.SenderMatches(Text(1, 0, "a", fromId: 5), Text(2, 0, "b", fromId: 6)));

    [Fact]
    public void SenderMatches_BothFromIdAbsent_PureChannelPost_True() =>
        Assert.True(CaptionResolver.SenderMatches(
            Text(1, 0, "a", fromId: null), Text(2, 0, "b", fromId: null)));

    [Fact]
    public void SenderMatches_PostAuthorEqual_True() =>
        Assert.True(CaptionResolver.SenderMatches(
            Text(1, 0, "a", fromId: null, postAuthor: "Editor"),
            Text(2, 0, "b", fromId: null, postAuthor: "Editor")));

    [Fact]
    public void SenderMatches_PostAuthorDiffer_Rejected() =>
        Assert.False(CaptionResolver.SenderMatches(
            Text(1, 0, "a", fromId: 5, postAuthor: "Alice"),
            Text(2, 0, "b", fromId: 5, postAuthor: "Bob")));

    [Fact]
    public void SenderMatches_OneHasFromIdOtherDoesNot_False() =>
        Assert.False(CaptionResolver.SenderMatches(
            Text(1, 0, "a", fromId: 5), Text(2, 0, "b", fromId: null)));

    [Fact]
    public void Channel_PureChannelPost_InferredAttaches()
    {
        // Keduanya tanpa from_id (post channel murni) → dianggap pengirim sama → inferred jalan.
        var media = Media(1, 0, fromId: null);
        var text = Text(2, 10, "deskripsi channel", fromId: null);
        Resolve(media, text);

        Assert.Equal(CaptionSource.Inferred, First(media).CaptionSource);
    }

    [Fact]
    public void Channel_DifferentPostAuthor_InferredRejected()
    {
        var media = Media(1, 0, fromId: null, postAuthor: "Alice");
        var text = Text(2, 10, "deskripsi", fromId: null, postAuthor: "Bob");
        Resolve(media, text);

        Assert.Equal(CaptionSource.None, First(media).CaptionSource);
    }

    // ===================== Batas jendela inferred =====================
    [Fact]
    public void Inferred_InsideWindow_Attaches()
    {
        var media = Media(1, 0);
        var text = Text(2, 59, "tepat di dalam jendela");
        Resolve(media, text);

        Assert.Equal(CaptionSource.Inferred, First(media).CaptionSource);
    }

    [Fact]
    public void Inferred_OutsideWindow_None()
    {
        var media = Media(1, 0);
        var text = Text(2, 61, "lewat 60 detik");
        Resolve(media, text);

        Assert.Equal(CaptionSource.None, First(media).CaptionSource);
    }

    [Fact]
    public void Inferred_InterveningMediaBlocks()
    {
        // textA → mediaX → P. mediaX menyela sehingga textA tidak bisa menempel ke P.
        var textA = Text(1, 0, "teks untuk mediaX");
        var mediaX = Media(2, 5);
        var p = Media(3, 10);
        Resolve(textA, mediaX, p);

        // P tidak mendapat textA karena ada media penyela.
        Assert.Equal(CaptionSource.None, First(p).CaptionSource);
        // textA justru menempel ke mediaX yang terdekat tanpa penyela.
        Assert.Equal(CaptionSource.Inferred, First(mediaX).CaptionSource);
    }

    [Fact]
    public void Inferred_TwoDirectional_NearestWins()
    {
        // textA (+0s, jauh) di atas, P (+30s), textB (+45s, dekat) di bawah → B menang.
        var textA = Text(1, 0, "teks atas jauh");
        var p = Media(2, 30);
        var textB = Text(3, 45, "teks bawah dekat");
        Resolve(textA, p, textB);

        var r = First(p);
        Assert.Equal(CaptionSource.Inferred, r.CaptionSource);
        Assert.Equal("teks bawah dekat", r.Caption);
        Assert.Equal(3, r.CaptionFromMessageId);
    }

    [Fact]
    public void Inferred_Disabled_NoInference()
    {
        var media = Media(1, 0);
        var text = Text(2, 10, "tidak akan terpakai");
        var resolver = new CaptionResolver();
        resolver.Resolve(new MessageWindow(new[] { media, text }),
            new CaptionOptions { InferredEnabled = false });

        Assert.Equal(CaptionSource.None, First(media).CaptionSource);
    }

    // ===================== Property 9 — album sebagai satu post =====================
    [Fact]
    public void Property9_AlbumMembersShareSingleCaption()
    {
        var a = Media(10, 0, caption: "satu untuk semua", groupId: 42, mediaCount: 1);
        var b = Media(11, 0, groupId: 42, mediaCount: 1);
        Resolve(a, b);

        Assert.Equal(First(a).Caption, First(b).Caption);
        Assert.Equal(CaptionSource.Album, First(a).CaptionSource);
        Assert.Equal(CaptionSource.Album, First(b).CaptionSource);
    }
}
