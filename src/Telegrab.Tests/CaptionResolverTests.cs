using Telegrab.Models;
using Telegrab.Services;

namespace Telegrab.Tests;

/// <summary>
/// Tes perilaku untuk <see cref="CaptionResolver"/> — algoritma asosiasi deskripsi
/// (Requirement 7). Memakai tipe DTO murni (<see cref="ResolverMessage"/>/<see cref="ResolverMedia"/>)
/// sehingga deterministik tanpa MAUI/WTelegram.
/// </summary>
public class CaptionResolverTests
{
    private static readonly DateTime Base = new(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Pesan bermedia (1 media) dengan opsi teks/album/pengirim/reply.</summary>
    private static ResolverMessage Media(
        int id, long mediaId, string? text = null, long? groupId = null,
        int secondsOffset = 0, long? fromId = 1, string? postAuthor = null, int? replyTo = null)
    {
        var m = new ResolverMessage
        {
            MessageId = id,
            DateUtc = Base.AddSeconds(secondsOffset),
            Text = text,
            GroupId = groupId,
            FromId = fromId,
            PostAuthor = postAuthor,
            ReplyToMsgId = replyTo,
        };
        m.Media.Add(new ResolverMedia { MediaId = mediaId });
        return m;
    }

    /// <summary>Pesan teks murni (tanpa media).</summary>
    private static ResolverMessage Text(
        int id, string text, int secondsOffset = 0,
        long? fromId = 1, string? postAuthor = null, int? replyTo = null)
        => new()
        {
            MessageId = id,
            DateUtc = Base.AddSeconds(secondsOffset),
            Text = text,
            FromId = fromId,
            PostAuthor = postAuthor,
            ReplyToMsgId = replyTo,
        };

    private static MessageWindow Window(params ResolverMessage[] msgs) => new(msgs);

    private static void Resolve(MessageWindow w, CaptionOptions? opts = null)
        => new CaptionResolver().Resolve(w, opts ?? CaptionOptions.Default);

    private static ResolverMedia M(ResolverMessage m) => m.Media[0];

    // --- Caption langsung (own / album) -----------------------------------

    [Fact]
    public void OwnCaption_SingleMediaWithText()
    {
        var m = Media(10, 100, text: "hello");
        Resolve(Window(m));

        Assert.Equal(CaptionSource.Own, M(m).CaptionSource);
        Assert.Equal("hello", M(m).Caption);
        Assert.Equal(10, M(m).CaptionFromMessageId);
    }

    [Fact]
    public void NoCaption_WhenIsolatedMediaWithoutText()
    {
        var m = Media(10, 100);
        Resolve(Window(m));

        Assert.Equal(CaptionSource.None, M(m).CaptionSource);
        Assert.Null(M(m).Caption);
    }

    [Fact]
    public void AlbumCaption_AppliesToAllMembers_FromFirstWithText()
    {
        var a = Media(10, 100, text: "album cap", groupId: 555, secondsOffset: 0);
        var b = Media(11, 101, groupId: 555, secondsOffset: 1);
        Resolve(Window(a, b));

        foreach (var msg in new[] { a, b })
        {
            Assert.Equal(CaptionSource.Album, M(msg).CaptionSource);
            Assert.Equal("album cap", M(msg).Caption);
            Assert.Equal(10, M(msg).CaptionFromMessageId);
        }
    }

    [Fact]
    public void AlbumCaption_FromSecondMember_WhenFirstHasNoText()
    {
        var a = Media(10, 100, groupId: 555, secondsOffset: 0);
        var b = Media(11, 101, text: "cap on b", groupId: 555, secondsOffset: 1);
        Resolve(Window(a, b));

        Assert.Equal(CaptionSource.Album, M(a).CaptionSource);
        Assert.Equal("cap on b", M(a).Caption);
        Assert.Equal(11, M(a).CaptionFromMessageId);
    }

    // --- Reply (case 4 / 8 / 11) ------------------------------------------

    [Fact]
    public void ReplyCaption_PureTextReplyingToMedia_SameSender()
    {
        var media = Media(10, 100, secondsOffset: 0, fromId: 1);
        var reply = Text(11, "described later", secondsOffset: 5, fromId: 1, replyTo: 10);
        Resolve(Window(media, reply));

        Assert.Equal(CaptionSource.Reply, M(media).CaptionSource);
        Assert.Equal("described later", M(media).Caption);
        Assert.Equal(11, M(media).CaptionFromMessageId);
    }

    [Fact]
    public void Reply_DifferentSender_NotAttached()
    {
        var media = Media(10, 100, secondsOffset: 0, fromId: 1);
        var reply = Text(11, "other person", secondsOffset: 5, fromId: 2, replyTo: 10);
        Resolve(Window(media, reply));

        Assert.Equal(CaptionSource.None, M(media).CaptionSource);
    }

    [Fact]
    public void ReplyNote_WhenMediaAlreadyHasOwnCaption()
    {
        var media = Media(10, 100, text: "own cap", secondsOffset: 0, fromId: 1);
        var reply = Text(11, "extra note", secondsOffset: 5, fromId: 1, replyTo: 10);
        Resolve(Window(media, reply));

        Assert.Equal(CaptionSource.Own, M(media).CaptionSource);
        Assert.Equal("own cap", M(media).Caption);
        Assert.Equal("extra note", M(media).Note);
        Assert.Equal(11, M(media).NoteFromMessageId);
    }

    [Fact]
    public void ReplyToAlbumMember_CaptionsWholeAlbum()
    {
        var a = Media(10, 100, groupId: 555, secondsOffset: 0, fromId: 1);
        var b = Media(11, 101, groupId: 555, secondsOffset: 1, fromId: 1);
        var reply = Text(12, "album reply", secondsOffset: 5, fromId: 1, replyTo: 11); // membalas anggota b
        Resolve(Window(a, b, reply));

        Assert.Equal(CaptionSource.Reply, M(a).CaptionSource);
        Assert.Equal("album reply", M(a).Caption);
        Assert.Equal(CaptionSource.Reply, M(b).CaptionSource);
    }

    [Fact]
    public void Reply_TargetNotInWindow_FallsThroughToInferred()
    {
        var media = Media(10, 100, secondsOffset: 0, fromId: 1);
        var reply = Text(11, "orphan reply", secondsOffset: 5, fromId: 1, replyTo: 9999);
        Resolve(Window(media, reply));

        // Target 9999 tak termuat → bukan reply; teks tetap tersedia → ditangkap inferred.
        Assert.Equal(CaptionSource.Inferred, M(media).CaptionSource);
        Assert.Equal("orphan reply", M(media).Caption);
    }

    // --- Inferred (case 3 / 5 / 6 / 7) ------------------------------------

    [Fact]
    public void Inferred_TextImmediatelyAfterMedia()
    {
        var media = Media(10, 100, secondsOffset: 0, fromId: 1);
        var text = Text(11, "caption below", secondsOffset: 10, fromId: 1);
        Resolve(Window(media, text));

        Assert.Equal(CaptionSource.Inferred, M(media).CaptionSource);
        Assert.Equal("caption below", M(media).Caption);
        Assert.Equal(11, M(media).CaptionFromMessageId);
    }

    [Fact]
    public void Inferred_TextImmediatelyBeforeMedia()
    {
        var text = Text(10, "caption above", secondsOffset: 0, fromId: 1);
        var media = Media(11, 100, secondsOffset: 10, fromId: 1);
        Resolve(Window(text, media));

        Assert.Equal(CaptionSource.Inferred, M(media).CaptionSource);
        Assert.Equal("caption above", M(media).Caption);
    }

    [Fact]
    public void Inferred_InterveningMediaBlocksFartherPost()
    {
        var a = Media(10, 100, secondsOffset: 0, fromId: 1);
        var b = Media(11, 101, secondsOffset: 5, fromId: 1);
        var t = Text(12, "for b", secondsOffset: 8, fromId: 1);
        Resolve(Window(a, b, t));

        Assert.Equal(CaptionSource.None, M(a).CaptionSource);    // diblokir media b
        Assert.Equal(CaptionSource.Inferred, M(b).CaptionSource);
        Assert.Equal("for b", M(b).Caption);
    }

    [Fact]
    public void Inferred_NotAttached_WhenBeyondTimeWindow()
    {
        var media = Media(10, 100, secondsOffset: 0, fromId: 1);
        var text = Text(11, "too late", secondsOffset: 120, fromId: 1);
        Resolve(Window(media, text));

        Assert.Equal(CaptionSource.None, M(media).CaptionSource);
    }

    [Fact]
    public void Inferred_Conflict_TextAttachesToNearestPost()
    {
        var a = Media(10, 100, secondsOffset: 0, fromId: 1);
        var t = Text(11, "shared", secondsOffset: 12, fromId: 1);
        var b = Media(12, 101, secondsOffset: 15, fromId: 1);
        Resolve(Window(a, t, b));

        // t: 12s dari a, 3s dari b → menempel ke b (terdekat)
        Assert.Equal(CaptionSource.Inferred, M(b).CaptionSource);
        Assert.Equal("shared", M(b).Caption);
        Assert.Equal(CaptionSource.None, M(a).CaptionSource);
    }

    [Fact]
    public void Inferred_DifferentSender_NotAttached()
    {
        var media = Media(10, 100, secondsOffset: 0, fromId: 1);
        var text = Text(11, "by someone else", secondsOffset: 5, fromId: 2);
        Resolve(Window(media, text));

        Assert.Equal(CaptionSource.None, M(media).CaptionSource);
    }

    [Fact]
    public void Inferred_Disabled_NoAttachment()
    {
        var media = Media(10, 100, secondsOffset: 0, fromId: 1);
        var text = Text(11, "below", secondsOffset: 5, fromId: 1);
        Resolve(Window(media, text), new CaptionOptions { InferredEnabled = false });

        Assert.Equal(CaptionSource.None, M(media).CaptionSource);
    }

    [Fact]
    public void Inferred_ConsumedByReply_NotReusedForAnotherPost()
    {
        var a = Media(10, 100, secondsOffset: 0, fromId: 1);
        var t = Text(11, "shared text", secondsOffset: 5, fromId: 1, replyTo: 10); // reply ke a
        var b = Media(12, 101, secondsOffset: 8, fromId: 1);
        Resolve(Window(a, t, b));

        Assert.Equal(CaptionSource.Reply, M(a).CaptionSource);   // t dikonsumsi a
        Assert.Equal(CaptionSource.None, M(b).CaptionSource);    // t tidak boleh dipakai ulang
    }

    // --- case 13: teks murni tanpa media ----------------------------------

    [Fact]
    public void PureTextOnly_NoMedia_DoesNotThrow()
    {
        var w = Window(Text(10, "hello", fromId: 1), Text(11, "world", secondsOffset: 2, fromId: 1));
        var ex = Record.Exception(() => Resolve(w));
        Assert.Null(ex);
    }

    // --- SenderMatches (Requirement 7.8) ----------------------------------

    [Fact]
    public void SenderMatches_PostAuthorTakesPrecedence()
    {
        var a = new ResolverMessage { PostAuthor = "Alice", FromId = 1 };
        var sameAuthorDifferentFrom = new ResolverMessage { PostAuthor = "Alice", FromId = 2 };
        var otherAuthor = new ResolverMessage { PostAuthor = "Bob", FromId = 1 };

        Assert.True(CaptionResolver.SenderMatches(a, sameAuthorDifferentFrom));
        Assert.False(CaptionResolver.SenderMatches(a, otherAuthor));
    }

    [Fact]
    public void SenderMatches_FromIdWhenNoAuthor()
    {
        Assert.True(CaptionResolver.SenderMatches(
            new ResolverMessage { FromId = 5 }, new ResolverMessage { FromId = 5 }));
        Assert.False(CaptionResolver.SenderMatches(
            new ResolverMessage { FromId = 5 }, new ResolverMessage { FromId = 6 }));
    }

    [Fact]
    public void SenderMatches_BothChannelPosts_NoFromId_AreSame()
        => Assert.True(CaptionResolver.SenderMatches(new ResolverMessage(), new ResolverMessage()));

    [Fact]
    public void SenderMatches_MixedFromId_AreDifferent()
        => Assert.False(CaptionResolver.SenderMatches(
            new ResolverMessage { FromId = 1 }, new ResolverMessage()));
}
