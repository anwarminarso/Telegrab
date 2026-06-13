using Telegrab.Models;
using Telegrab.Services;

namespace Telegrab.Tests;

/// <summary>
/// Unit test untuk <see cref="ManifestDbService"/> (task 3.1).
///
/// Memakai direktori sementara nyata + file SQLite nyata; membuat file fisik di disk untuk
/// memvalidasi perilaku <c>File.Exists</c>. Tiap test membersihkan temp folder-nya sendiri.
///
/// Memvalidasi:
///  - Property 1 (idempotensi manifest): upsert kunci sama → tepat satu baris.
///  - Property 2 (konsistensi disk↔dok): IsDownloaded & QueryFolder hanya mengakui file yang ada.
/// </summary>
public sealed class ManifestDbServiceTests : IDisposable
{
    private readonly string _root;

    public ManifestDbServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "telegrab_tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            // Pastikan pool koneksi dilepas agar file DB tidak terkunci sebelum dihapus.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; jangan gagalkan test karena lock sisa.
        }
    }

    private static MediaRecord NewRecord(
        long chatId,
        int messageId,
        long mediaId,
        string relativePath,
        string fileName,
        DateTime? messageDate = null,
        string? caption = null,
        CaptionSource captionSource = CaptionSource.None,
        long? groupId = null)
    {
        return new MediaRecord
        {
            ChatId = chatId,
            MessageId = messageId,
            MediaId = mediaId,
            GroupId = groupId,
            ChatTitle = "Chat",
            TopicTitle = null,
            RelativePath = relativePath,
            FileName = fileName,
            Size = 1234,
            Type = "Photo",
            Width = 100,
            Height = 200,
            DurationSeconds = null,
            Sender = "Alice",
            Caption = caption,
            CaptionSource = captionSource,
            CaptionFromMessageId = null,
            Note = null,
            NoteFromMessageId = null,
            MessageDateUtc = messageDate ?? new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            DownloadedAtUtc = new DateTime(2024, 1, 2, 8, 0, 0, DateTimeKind.Utc),
        };
    }

    /// <summary>Buat file fisik (dan folder induknya) di bawah root, relatif terhadap root.</summary>
    private string CreateFile(string relativePath)
    {
        var abs = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, "x");
        return abs;
    }

    [Fact]
    public void OpenForRoot_SetsIsReady_AndCreatesDbFile()
    {
        using var service = new ManifestDbService();
        Assert.False(service.IsReady);

        service.OpenForRoot(_root);

        Assert.True(service.IsReady);
        Assert.True(File.Exists(Path.Combine(_root, "telegrab.db")));
    }

    [Fact]
    public void Close_MakesServiceNotReady()
    {
        using var service = new ManifestDbService();
        service.OpenForRoot(_root);
        Assert.True(service.IsReady);

        service.Close();

        Assert.False(service.IsReady);
    }

    [Fact]
    public void Operations_BeforeOpen_Throw()
    {
        using var service = new ManifestDbService();
        Assert.Throws<InvalidOperationException>(
            () => service.IsDownloaded(1, 1, 1, out _));
        Assert.Throws<InvalidOperationException>(
            () => service.Mark(NewRecord(1, 1, 1, "Chat/a.jpg", "a.jpg")));
        Assert.Throws<InvalidOperationException>(
            () => service.QueryFolder("Chat"));
    }

    // --- Property 1: idempotensi manifest ---------------------------------

    [Fact]
    public void Mark_SameKeyMultipleTimes_ProducesExactlyOneRow()
    {
        CreateFile("Chat/a.jpg");
        using var service = new ManifestDbService();
        service.OpenForRoot(_root);

        var record = NewRecord(10, 20, 30, "Chat/a.jpg", "a.jpg", caption: "first");
        service.Mark(record);
        service.Mark(record);
        service.Mark(record);

        var rows = service.QueryFolder("Chat");
        Assert.Single(rows);
        Assert.Equal("first", rows[0].Caption);
    }

    [Fact]
    public void Mark_SameKey_UpdatesInsteadOfDuplicating()
    {
        CreateFile("Chat/a.jpg");
        using var service = new ManifestDbService();
        service.OpenForRoot(_root);

        service.Mark(NewRecord(10, 20, 30, "Chat/a.jpg", "a.jpg", caption: "first"));
        service.Mark(NewRecord(10, 20, 30, "Chat/a.jpg", "a.jpg", caption: "updated", captionSource: CaptionSource.Own));

        var rows = service.QueryFolder("Chat");
        Assert.Single(rows);
        Assert.Equal("updated", rows[0].Caption);
        Assert.Equal(CaptionSource.Own, rows[0].CaptionSource);
    }

    // --- Property 2: konsistensi disk↔dok ---------------------------------

    [Fact]
    public void IsDownloaded_TrueWhenFileExists_FalseWhenMissing()
    {
        using var service = new ManifestDbService();
        service.OpenForRoot(_root);

        // File ada → true.
        CreateFile("Chat/a.jpg");
        service.Mark(NewRecord(1, 2, 3, "Chat/a.jpg", "a.jpg"));
        Assert.True(service.IsDownloaded(1, 2, 3, out var absExisting));
        Assert.True(File.Exists(absExisting));

        // Tercatat tapi file dihapus → false (tetap mengembalikan path resolusi).
        service.Mark(NewRecord(4, 5, 6, "Chat/ghost.jpg", "ghost.jpg"));
        Assert.False(service.IsDownloaded(4, 5, 6, out var absGhost));
        Assert.False(string.IsNullOrEmpty(absGhost));
        Assert.False(File.Exists(absGhost));
    }

    [Fact]
    public void IsDownloaded_UnknownKey_ReturnsFalseAndEmptyPath()
    {
        using var service = new ManifestDbService();
        service.OpenForRoot(_root);

        Assert.False(service.IsDownloaded(999, 999, 999, out var abs));
        Assert.Equal(string.Empty, abs);
    }

    [Fact]
    public void IsDownloaded_ResolvesRelativePathAgainstRoot()
    {
        using var service = new ManifestDbService();
        service.OpenForRoot(_root);

        CreateFile("Chat/Topic/pic.jpg");
        service.Mark(NewRecord(1, 1, 1, "Chat/Topic/pic.jpg", "pic.jpg"));

        Assert.True(service.IsDownloaded(1, 1, 1, out var abs));
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "Chat", "Topic", "pic.jpg")), abs);
    }

    [Fact]
    public void QueryFolder_FiltersOutMissingFiles()
    {
        using var service = new ManifestDbService();
        service.OpenForRoot(_root);

        CreateFile("Chat/present.jpg");
        service.Mark(NewRecord(1, 1, 1, "Chat/present.jpg", "present.jpg"));
        // Tidak membuat file fisik untuk record kedua.
        service.Mark(NewRecord(1, 2, 2, "Chat/missing.jpg", "missing.jpg"));

        var rows = service.QueryFolder("Chat");
        Assert.Single(rows);
        Assert.Equal("present.jpg", rows[0].FileName);
    }

    [Fact]
    public void QueryFolder_ReturnsOnlyDirectChildren()
    {
        using var service = new ManifestDbService();
        service.OpenForRoot(_root);

        CreateFile("Chat/direct.jpg");
        CreateFile("Chat/Sub/nested.jpg");
        service.Mark(NewRecord(1, 1, 1, "Chat/direct.jpg", "direct.jpg"));
        service.Mark(NewRecord(1, 2, 2, "Chat/Sub/nested.jpg", "nested.jpg"));

        var chatRows = service.QueryFolder("Chat");
        Assert.Single(chatRows);
        Assert.Equal("direct.jpg", chatRows[0].FileName);

        var subRows = service.QueryFolder("Chat/Sub");
        Assert.Single(subRows);
        Assert.Equal("nested.jpg", subRows[0].FileName);
    }

    [Fact]
    public void QueryFolder_OrdersByDateThenMessageIdThenMediaId()
    {
        using var service = new ManifestDbService();
        service.OpenForRoot(_root);

        CreateFile("Chat/c.jpg");
        CreateFile("Chat/a.jpg");
        CreateFile("Chat/b.jpg");
        CreateFile("Chat/d.jpg");

        var early = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var late = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        // Sisipkan dengan urutan acak; harapkan keluar terurut date, message_id, media_id.
        service.Mark(NewRecord(1, 5, 1, "Chat/c.jpg", "c.jpg", messageDate: late));        // last (late date)
        service.Mark(NewRecord(1, 2, 9, "Chat/a.jpg", "a.jpg", messageDate: early));        // msg 2, media 9
        service.Mark(NewRecord(1, 2, 1, "Chat/b.jpg", "b.jpg", messageDate: early));        // msg 2, media 1
        service.Mark(NewRecord(1, 3, 1, "Chat/d.jpg", "d.jpg", messageDate: early));        // msg 3

        var rows = service.QueryFolder("Chat");
        Assert.Equal(4, rows.Count);
        Assert.Equal("b.jpg", rows[0].FileName); // early, msg2, media1
        Assert.Equal("a.jpg", rows[1].FileName); // early, msg2, media9
        Assert.Equal("d.jpg", rows[2].FileName); // early, msg3
        Assert.Equal("c.jpg", rows[3].FileName); // late
    }

    [Fact]
    public void QueryFolder_RootLevelFiles_ReturnedForEmptyFolder()
    {
        using var service = new ManifestDbService();
        service.OpenForRoot(_root);

        CreateFile("rootfile.jpg");
        CreateFile("Chat/inside.jpg");
        service.Mark(NewRecord(1, 1, 1, "rootfile.jpg", "rootfile.jpg"));
        service.Mark(NewRecord(1, 2, 2, "Chat/inside.jpg", "inside.jpg"));

        var rootRows = service.QueryFolder("");
        Assert.Single(rootRows);
        Assert.Equal("rootfile.jpg", rootRows[0].FileName);
    }

    // --- Open/close pada pergantian root ----------------------------------

    [Fact]
    public void OpenForRoot_SwitchingRoots_IsolatesData()
    {
        var rootB = Path.Combine(Path.GetTempPath(), "telegrab_tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootB);
        try
        {
            using var service = new ManifestDbService();

            // Root A: catat satu media + buat file-nya.
            service.OpenForRoot(_root);
            CreateFile("Chat/a.jpg");
            service.Mark(NewRecord(1, 1, 1, "Chat/a.jpg", "a.jpg"));
            Assert.True(service.IsDownloaded(1, 1, 1, out _));

            // Beralih ke root B: DB baru, tidak ada data A.
            service.OpenForRoot(rootB);
            Assert.True(service.IsReady);
            Assert.Equal(Path.GetFullPath(rootB), service.Root);
            Assert.False(service.IsDownloaded(1, 1, 1, out _));
            Assert.Empty(service.QueryFolder("Chat"));

            // Kembali ke root A: status pulih dari DB yang sudah ada.
            service.OpenForRoot(_root);
            Assert.True(service.IsDownloaded(1, 1, 1, out _));
            Assert.Single(service.QueryFolder("Chat"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(rootB))
                Directory.Delete(rootB, recursive: true);
        }
    }

    [Fact]
    public void OpenForRoot_ReopeningSameRoot_RecoversPersistedRows()
    {
        CreateFile("Chat/a.jpg");

        using (var service = new ManifestDbService())
        {
            service.OpenForRoot(_root);
            service.Mark(NewRecord(1, 1, 1, "Chat/a.jpg", "a.jpg", caption: "persist"));
        }

        using (var reopened = new ManifestDbService())
        {
            reopened.OpenForRoot(_root);
            var rows = reopened.QueryFolder("Chat");
            Assert.Single(rows);
            Assert.Equal("persist", rows[0].Caption);
        }
    }
}
