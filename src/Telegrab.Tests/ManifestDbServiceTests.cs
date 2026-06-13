using Telegrab.Models;
using Telegrab.Services;

namespace Telegrab.Tests;

/// <summary>
/// Tes perilaku untuk <see cref="ManifestDbService"/> — manifest SQLite (telegrab.db).
/// Memakai root sementara nyata di disk per-test (xUnit membuat instance baru tiap metode),
/// dengan file fisik dibuat karena service memfilter berdasarkan <c>File.Exists</c>.
/// </summary>
public sealed class ManifestDbServiceTests : IDisposable
{
    private readonly string _root;
    private readonly ManifestDbService _db;

    public ManifestDbServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "telegrab_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _db = new ManifestDbService();
        _db.OpenForRoot(_root);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string CreateFile(string relativePath, string content = "x")
    {
        var abs = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
        return abs;
    }

    private static MediaRecord Record(
        long chatId, int messageId, long mediaId, string relativePath,
        string type = "Photo", long? groupId = null, string? caption = null,
        CaptionSource source = CaptionSource.None, DateTime? messageDate = null)
        => new()
        {
            ChatId = chatId,
            MessageId = messageId,
            MediaId = mediaId,
            RelativePath = relativePath,
            FileName = Path.GetFileName(relativePath.Replace('\\', '/')),
            Size = 123,
            Type = type,
            GroupId = groupId,
            Caption = caption,
            CaptionSource = source,
            MessageDateUtc = messageDate ?? new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DownloadedAtUtc = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        };

    // --- Lifecycle ---------------------------------------------------------

    [Fact]
    public void OpenForRoot_IsReady_AndRootSetToFullPath()
    {
        Assert.True(_db.IsReady);
        Assert.Equal(Path.GetFullPath(_root), _db.Root);
    }

    [Fact]
    public void OpenForRoot_Throws_OnEmptyRoot()
    {
        using var db = new ManifestDbService();
        Assert.Throws<ArgumentException>(() => db.OpenForRoot(""));
    }

    [Fact]
    public void Operations_Throw_WhenNotOpened()
    {
        using var db = new ManifestDbService();
        Assert.False(db.IsReady);
        Assert.Throws<InvalidOperationException>(() => db.IsDownloaded(1, 1, 1, out _));
        Assert.Throws<InvalidOperationException>(() => db.QueryFolder("x"));
    }

    [Fact]
    public void Mark_Throws_OnNullRecord()
        => Assert.Throws<ArgumentNullException>(() => _db.Mark(null!));

    // --- IsDownloaded ------------------------------------------------------

    [Fact]
    public void Mark_ThenIsDownloaded_ReturnsTrue_WhenFileExists()
    {
        CreateFile("ChatA/photo.jpg");
        _db.Mark(Record(1, 10, 100, "ChatA/photo.jpg"));

        var ok = _db.IsDownloaded(1, 10, 100, out var abs);

        Assert.True(ok);
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "ChatA", "photo.jpg")), abs);
    }

    [Fact]
    public void IsDownloaded_False_WhenNotRecorded()
    {
        Assert.False(_db.IsDownloaded(1, 10, 100, out var abs));
        Assert.Equal(string.Empty, abs);
    }

    [Fact]
    public void IsDownloaded_False_WhenRecordedButFileMissing()
    {
        var abs = CreateFile("ChatA/gone.jpg");
        _db.Mark(Record(1, 10, 100, "ChatA/gone.jpg"));
        File.Delete(abs);

        Assert.False(_db.IsDownloaded(1, 10, 100, out _));
    }

    [Fact]
    public void Mark_NormalizesBackslashPaths()
    {
        CreateFile("ChatA/win.jpg");
        _db.Mark(Record(1, 10, 100, "ChatA\\win.jpg"));

        Assert.True(_db.IsDownloaded(1, 10, 100, out _));

        var rows = _db.QueryFolder("ChatA");
        Assert.Single(rows);
        Assert.Equal("ChatA/win.jpg", rows[0].RelativePath);
    }

    // --- Upsert idempotency ------------------------------------------------

    [Fact]
    public void Mark_Upsert_UpdatesExistingRowInPlace()
    {
        CreateFile("ChatA/p.jpg");
        _db.Mark(Record(1, 10, 100, "ChatA/p.jpg", caption: "first", source: CaptionSource.Own));
        _db.Mark(Record(1, 10, 100, "ChatA/p.jpg", caption: "second", source: CaptionSource.Reply));

        var rows = _db.QueryFolder("ChatA");
        Assert.Single(rows);
        Assert.Equal("second", rows[0].Caption);
        Assert.Equal(CaptionSource.Reply, rows[0].CaptionSource);
    }

    // --- QueryFolder -------------------------------------------------------

    [Fact]
    public void QueryFolder_ReturnsDirectChildrenOnly_OrderedByDate()
    {
        CreateFile("ChatA/b.jpg");
        CreateFile("ChatA/a.jpg");
        CreateFile("ChatA/Topic/c.jpg");
        _db.Mark(Record(1, 20, 200, "ChatA/b.jpg", messageDate: new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc)));
        _db.Mark(Record(1, 10, 100, "ChatA/a.jpg", messageDate: new DateTime(2024, 1, 1, 9, 0, 0, DateTimeKind.Utc)));
        _db.Mark(Record(1, 30, 300, "ChatA/Topic/c.jpg", messageDate: new DateTime(2024, 1, 1, 8, 0, 0, DateTimeKind.Utc)));

        var rows = _db.QueryFolder("ChatA");

        Assert.Equal(2, rows.Count);
        Assert.Equal("a.jpg", rows[0].FileName); // 09:00 sebelum 10:00
        Assert.Equal("b.jpg", rows[1].FileName);

        var nested = _db.QueryFolder("ChatA/Topic");
        Assert.Single(nested);
        Assert.Equal("c.jpg", nested[0].FileName);
    }

    [Fact]
    public void QueryFolder_ExcludesMissingFiles()
    {
        CreateFile("ChatA/exists.jpg");
        var goneAbs = CreateFile("ChatA/gone.jpg");
        _db.Mark(Record(1, 10, 100, "ChatA/exists.jpg"));
        _db.Mark(Record(1, 11, 101, "ChatA/gone.jpg"));
        File.Delete(goneAbs);

        var rows = _db.QueryFolder("ChatA");

        Assert.Single(rows);
        Assert.Equal("exists.jpg", rows[0].FileName);
    }

    [Fact]
    public void QueryFolder_Root_ReturnsFilesDirectlyInRoot()
    {
        CreateFile("top.jpg");
        CreateFile("ChatA/inner.jpg");
        _db.Mark(Record(1, 10, 100, "top.jpg"));
        _db.Mark(Record(1, 11, 101, "ChatA/inner.jpg"));

        var rows = _db.QueryFolder("");

        Assert.Single(rows);
        Assert.Equal("top.jpg", rows[0].FileName);
    }

    // --- Round-trips -------------------------------------------------------

    [Theory]
    [InlineData(CaptionSource.Own)]
    [InlineData(CaptionSource.Album)]
    [InlineData(CaptionSource.Reply)]
    [InlineData(CaptionSource.Inferred)]
    [InlineData(CaptionSource.None)]
    public void CaptionSource_RoundTrips(CaptionSource source)
    {
        CreateFile("ChatA/p.jpg");
        _db.Mark(Record(1, 10, 100, "ChatA/p.jpg", caption: "c", source: source));

        var rows = _db.QueryFolder("ChatA");
        Assert.Equal(source, rows[0].CaptionSource);
    }

    [Fact]
    public void GroupId_RoundTrips_ValueAndNull()
    {
        CreateFile("ChatA/p.jpg");
        CreateFile("ChatA/q.jpg");
        _db.Mark(Record(1, 10, 100, "ChatA/p.jpg", groupId: 999));
        _db.Mark(Record(1, 11, 101, "ChatA/q.jpg", groupId: null));

        var rows = _db.QueryFolder("ChatA");
        Assert.Equal(999, rows.First(r => r.MediaId == 100).GroupId);
        Assert.Null(rows.First(r => r.MediaId == 101).GroupId);
    }

    [Fact]
    public void MessageDate_RoundTripsAsUtc_WithSubSecondPrecision()
    {
        CreateFile("ChatA/p.jpg");
        var date = new DateTime(2024, 3, 15, 8, 30, 45, DateTimeKind.Utc).AddTicks(1234567);
        _db.Mark(Record(1, 10, 100, "ChatA/p.jpg", messageDate: date));

        var rows = _db.QueryFolder("ChatA");
        Assert.Equal(date, rows[0].MessageDateUtc);
        Assert.Equal(DateTimeKind.Utc, rows[0].MessageDateUtc.Kind);
    }

    // --- Persistence -------------------------------------------------------

    [Fact]
    public void Persistence_SurvivesCloseAndReopen()
    {
        CreateFile("ChatA/p.jpg");
        _db.Mark(Record(1, 10, 100, "ChatA/p.jpg"));

        _db.Close();
        _db.OpenForRoot(_root);

        Assert.True(_db.IsDownloaded(1, 10, 100, out _));
    }
}
