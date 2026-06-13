using System.Globalization;
using Telegrab.Models;
using Telegrab.Services;

namespace Telegrab.Tests;

/// <summary>
/// Unit test untuk generasi <c>README.md</c> (task 10.1).
///
/// Sebagian besar test menargetkan LOGIKA MURNI <see cref="DocumentationRenderer.Render"/>
/// (transformasi string → string) secara deterministik dengan culture invariant dan konverter
/// waktu identitas (tanpa bergantung pada zona waktu/locale mesin). Sebagian kecil test
/// menvalidasi pembungkus IO <see cref="DocumentationService"/> melalui DB + file nyata.
///
/// Memvalidasi:
///  - Property 2 (konsistensi disk↔dok): README hanya memuat media yang dirender
///    (pemfilteran <c>File.Exists</c> dilakukan <c>QueryFolder</c>; renderer merender persis input).
///  - Property 4 (preservasi teks pengguna): regenerasi tidak mengubah teks di luar penanda.
///  - Property 9 (album sebagai satu post): anggota album berbagi caption &amp; satu grup.
/// </summary>
public sealed class DocumentationServiceTests
{
    // Konverter deterministik: perlakukan waktu apa adanya (tanpa konversi zona waktu).
    private static readonly Func<DateTime, DateTime> Identity = static d => d;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string Render(IReadOnlyList<MediaRecord> records, string? existing) =>
        DocumentationRenderer.Render(records, existing, Inv, Identity);

    private static MediaRecord Record(
        int messageId,
        long mediaId,
        string fileName,
        DateTime? date = null,
        string? caption = null,
        CaptionSource captionSource = CaptionSource.None,
        long? groupId = null,
        string? note = null,
        string type = "Photo",
        long size = 1024,
        string? sender = "Alice",
        string? relativePath = null)
    {
        return new MediaRecord
        {
            ChatId = 1,
            MessageId = messageId,
            MediaId = mediaId,
            GroupId = groupId,
            ChatTitle = "Chat",
            RelativePath = relativePath ?? ("Chat/" + fileName),
            FileName = fileName,
            Size = size,
            Type = type,
            Sender = sender,
            Caption = caption,
            CaptionSource = captionSource,
            Note = note,
            MessageDateUtc = date ?? new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            DownloadedAtUtc = new DateTime(2024, 1, 2, 8, 0, 0, DateTimeKind.Utc),
        };
    }

    // --- Property 4: preservasi teks pengguna -----------------------------

    [Fact]
    public void Render_PreservesTextOutsideMarkers_ReplacesInside()
    {
        var existing =
            "# Catatan Pengguna\n" +
            "Teks bebas sebelum.\n\n" +
            DocumentationRenderer.BeginMarker + "\n" +
            "KONTEN LAMA YANG HARUS DIGANTI\n" +
            DocumentationRenderer.EndMarker + "\n\n" +
            "Teks bebas sesudah.\n";

        var result = Render(new[] { Record(1, 1, "a.jpg", caption: "halo") }, existing);

        // Teks luar dipertahankan persis.
        Assert.StartsWith("# Catatan Pengguna\nTeks bebas sebelum.\n\n", result);
        Assert.EndsWith("\n\nTeks bebas sesudah.\n", result);

        // Konten lama hilang, konten baru masuk.
        Assert.DoesNotContain("KONTEN LAMA", result);
        Assert.Contains("halo", result);
        Assert.Contains("[a.jpg](a.jpg)", result);
    }

    [Fact]
    public void Render_PreservesOuterText_AcrossRepeatedRegeneration()
    {
        var existing =
            "USER-HEADER\n" +
            DocumentationRenderer.BeginMarker + "\n" + "old\n" + DocumentationRenderer.EndMarker + "\n" +
            "USER-FOOTER\n";

        var first = Render(new[] { Record(1, 1, "a.jpg", caption: "satu") }, existing);
        var second = Render(new[] { Record(2, 2, "b.jpg", caption: "dua") }, first);

        Assert.StartsWith("USER-HEADER\n", second);
        Assert.EndsWith("USER-FOOTER\n", second);
        Assert.Contains("dua", second);
        Assert.DoesNotContain("satu", second); // regenerasi mengganti isi blok
    }

    // --- Fallback: file/penanda hilang ------------------------------------

    [Fact]
    public void Render_NoExistingFile_WritesFullFileWithMarkers()
    {
        var result = Render(new[] { Record(1, 1, "a.jpg", caption: "x") }, existing: null);

        Assert.Contains(DocumentationRenderer.BeginMarker, result);
        Assert.Contains(DocumentationRenderer.EndMarker, result);
        Assert.StartsWith(DocumentationRenderer.BeginMarker, result);
    }

    [Fact]
    public void Render_ExistingContentWithoutMarkers_RewritesFullFile()
    {
        var existing = "Konten lama tanpa penanda sama sekali.\n";

        var result = Render(new[] { Record(1, 1, "a.jpg", caption: "x") }, existing);

        // Tidak ada penanda → tulis ulang lengkap dengan blok penanda baru.
        Assert.StartsWith(DocumentationRenderer.BeginMarker, result);
        Assert.Contains(DocumentationRenderer.EndMarker, result);
        Assert.DoesNotContain("Konten lama tanpa penanda", result);
    }

    [Fact]
    public void Render_OnlyBeginMarker_TreatedAsMissing()
    {
        var existing = "before\n" + DocumentationRenderer.BeginMarker + "\nhalf\n";

        var result = Render(new[] { Record(1, 1, "a.jpg") }, existing);

        // Penanda tidak lengkap → fallback tulis ulang penuh.
        Assert.StartsWith(DocumentationRenderer.BeginMarker, result);
        Assert.DoesNotContain("before", result);
    }

    // --- Requirement 8.8: escaping --------------------------------------------

    [Fact]
    public void Render_EscapesPipeAndNewlinesInCaption()
    {
        var caption = "kolom1 | kolom2\nbaris kedua";
        var result = Render(new[] { Record(1, 1, "a.jpg", caption: caption) }, null);

        Assert.Contains("kolom1 \\| kolom2 baris kedua", result);
        // Tidak ada newline mentah di dalam caption (akan merusak layout).
        Assert.DoesNotContain("kolom2\nbaris", result);
    }

    [Fact]
    public void Render_EscapesPipeAndNewlinesInNote()
    {
        var result = Render(
            new[] { Record(1, 1, "a.jpg", caption: "c", note: "catatan a | b\nc") },
            null);

        Assert.Contains("> catatan: catatan a \\| b c", result);
    }

    // --- Property 2: hanya media yang diberikan yang dirender -------------

    [Fact]
    public void Render_RendersExactlyTheRecordsProvided()
    {
        var records = new[]
        {
            Record(1, 1, "present1.jpg"),
            Record(2, 2, "present2.jpg"),
        };

        var result = Render(records, null);

        Assert.Contains("[present1.jpg](present1.jpg)", result);
        Assert.Contains("[present2.jpg](present2.jpg)", result);
        // Tidak ada entri lain yang muncul.
        Assert.DoesNotContain("missing", result);
    }

    [Fact]
    public void Render_EmptyRecords_ProducesEmptyMarkerBlock()
    {
        var result = Render(Array.Empty<MediaRecord>(), null);

        Assert.Contains(DocumentationRenderer.BeginMarker, result);
        Assert.Contains(DocumentationRenderer.EndMarker, result);
        Assert.DoesNotContain("## ", result);
    }

    // --- Property 9: album sebagai satu grup ------------------------------

    [Fact]
    public void Render_AlbumMembers_RenderedAsSingleGroup()
    {
        var date = new DateTime(2024, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        var records = new[]
        {
            Record(10, 1, "alb1.jpg", date: date, caption: "deskripsi album", captionSource: CaptionSource.Album, groupId: 555),
            Record(10, 2, "alb2.jpg", date: date, caption: "deskripsi album", captionSource: CaptionSource.Album, groupId: 555),
            Record(11, 3, "alb3.jpg", date: date, caption: "deskripsi album", captionSource: CaptionSource.Album, groupId: 555),
        };

        var result = Render(records, null);

        // Satu heading saja untuk seluruh album.
        var headingCount = result.Split("## ").Length - 1;
        Assert.Equal(1, headingCount);

        // Caption muncul sekali untuk grup.
        var captionCount = CountOccurrences(result, "deskripsi album");
        Assert.Equal(1, captionCount);

        // Semua anggota album terdaftar sebagai item file.
        Assert.Contains("[alb1.jpg](alb1.jpg)", result);
        Assert.Contains("[alb2.jpg](alb2.jpg)", result);
        Assert.Contains("[alb3.jpg](alb3.jpg)", result);
    }

    [Fact]
    public void Render_NonAlbumRecords_EachOwnGroup()
    {
        var records = new[]
        {
            Record(1, 1, "a.jpg", caption: "satu"),
            Record(2, 2, "b.jpg", caption: "dua"),
        };

        var result = Render(records, null);

        var headingCount = result.Split("## ").Length - 1;
        Assert.Equal(2, headingCount);
    }

    // --- Ordering & format ------------------------------------------------

    [Fact]
    public void Render_PreservesInputOrderingOfGroups()
    {
        var early = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var late = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        // Input sudah terurut dari QueryFolder (date, message_id, media_id).
        var records = new[]
        {
            Record(1, 1, "first.jpg", date: early, caption: "pertama"),
            Record(2, 2, "second.jpg", date: late, caption: "kedua"),
        };

        var result = Render(records, null);

        var idxFirst = result.IndexOf("first.jpg", StringComparison.Ordinal);
        var idxSecond = result.IndexOf("second.jpg", StringComparison.Ordinal);
        Assert.True(idxFirst >= 0 && idxSecond > idxFirst, "Urutan grup harus mengikuti urutan input.");
    }

    [Fact]
    public void Render_NoCaption_ShowsPlaceholder()
    {
        var result = Render(new[] { Record(1, 1, "a.jpg") }, null);
        Assert.Contains("_(tanpa deskripsi)_", result);
    }

    [Fact]
    public void Render_ReplyAndInferred_ShowSourceLabels()
    {
        var reply = Render(new[] { Record(1, 1, "a.jpg", caption: "balasan", captionSource: CaptionSource.Reply) }, null);
        Assert.Contains("_(membalas)_", reply);

        var inferred = Render(new[] { Record(2, 2, "b.jpg", caption: "tebakan", captionSource: CaptionSource.Inferred) }, null);
        Assert.Contains("_(inferred)_", inferred);
    }

    [Fact]
    public void Render_FileListLine_HasTypeAndSize()
    {
        var result = Render(new[] { Record(1, 1, "clip.mp4", type: "Video", size: 2048) }, null);
        Assert.Contains("[clip.mp4](clip.mp4) — Video · 2 KB", result);
    }

    // --- DocumentationService (IO wrapper) --------------------------------

    [Fact]
    public async Task RebuildFolderAsync_WritesReadme_OnlyForExistingFiles()
    {
        var root = NewTempRoot();
        try
        {
            CreateFile(root, "Chat/present.jpg");
            // "missing.jpg" sengaja TIDAK dibuat.

            using var db = new ManifestDbService();
            db.OpenForRoot(root);
            db.Mark(Record(1, 1, "present.jpg", caption: "ada"));
            db.Mark(Record(2, 2, "missing.jpg", caption: "hilang"));

            using var docs = new DocumentationService(db);
            await docs.RebuildFolderAsync("Chat");

            var readme = File.ReadAllText(Path.Combine(root, "Chat", "README.md"));

            // Property 2: hanya media yang File.Exists yang dirender.
            Assert.Contains("present.jpg", readme);
            Assert.DoesNotContain("missing.jpg", readme);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task RebuildFolderAsync_PreservesUserTextOutsideMarkers()
    {
        var root = NewTempRoot();
        try
        {
            CreateFile(root, "Chat/a.jpg");
            var folder = Path.Combine(root, "Chat");
            Directory.CreateDirectory(folder);
            var readmePath = Path.Combine(folder, "README.md");
            File.WriteAllText(readmePath,
                "MY NOTES\n" +
                DocumentationRenderer.BeginMarker + "\nold\n" + DocumentationRenderer.EndMarker + "\n" +
                "MY FOOTER\n");

            using var db = new ManifestDbService();
            db.OpenForRoot(root);
            db.Mark(Record(1, 1, "a.jpg", caption: "baru"));

            using var docs = new DocumentationService(db);
            await docs.RebuildFolderAsync("Chat");

            var readme = File.ReadAllText(readmePath);
            Assert.StartsWith("MY NOTES\n", readme);
            Assert.EndsWith("MY FOOTER\n", readme);
            Assert.Contains("baru", readme);
            Assert.DoesNotContain("old", readme);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task RebuildFolderAsync_NoReadme_CreatesItWithMarkers()
    {
        var root = NewTempRoot();
        try
        {
            CreateFile(root, "Chat/a.jpg");

            using var db = new ManifestDbService();
            db.OpenForRoot(root);
            db.Mark(Record(1, 1, "a.jpg", caption: "x"));

            using var docs = new DocumentationService(db);
            await docs.RebuildFolderAsync("Chat");

            var readmePath = Path.Combine(root, "Chat", "README.md");
            Assert.True(File.Exists(readmePath));
            var readme = File.ReadAllText(readmePath);
            Assert.Contains(DocumentationRenderer.BeginMarker, readme);
            Assert.Contains(DocumentationRenderer.EndMarker, readme);
        }
        finally
        {
            Cleanup(root);
        }
    }

    // --- Editor (Fase 3): deteksi suntingan di dalam blok penanda (Req 11.4) ---

    [Fact]
    public void TryGetInsideMarkers_ReturnsInnerContent_WhenMarkersPresent()
    {
        var content =
            "header\n" + DocumentationRenderer.BeginMarker + "\nINNER\n" +
            DocumentationRenderer.EndMarker + "\nfooter\n";

        var ok = DocumentationRenderer.TryGetInsideMarkers(content, out var inside);

        Assert.True(ok);
        Assert.Contains("INNER", inside);
        Assert.DoesNotContain("header", inside);
        Assert.DoesNotContain("footer", inside);
    }

    [Theory]
    [InlineData("no markers at all")]
    [InlineData("only begin <!-- TELEGRAB:BEGIN -->\nhalf")]
    public void TryGetInsideMarkers_ReturnsFalse_WhenNoValidPair(string content)
    {
        Assert.False(DocumentationRenderer.TryGetInsideMarkers(content, out _));
    }

    [Fact]
    public void InsideMarkersModified_FalseWhenOuterTextChangesOnly()
    {
        var original =
            "HEADER A\n" + DocumentationRenderer.BeginMarker + "\ngenerated\n" +
            DocumentationRenderer.EndMarker + "\nFOOTER A\n";
        // Pengguna hanya mengubah teks DI LUAR penanda → tidak dianggap modifikasi blok.
        var edited =
            "HEADER B (diedit)\n" + DocumentationRenderer.BeginMarker + "\ngenerated\n" +
            DocumentationRenderer.EndMarker + "\nFOOTER B (diedit)\n";

        Assert.False(DocumentationRenderer.InsideMarkersModified(original, edited));
    }

    [Fact]
    public void InsideMarkersModified_IgnoresTrivialWhitespaceAndLineEndings()
    {
        var original =
            DocumentationRenderer.BeginMarker + "\ngenerated\n" + DocumentationRenderer.EndMarker;
        var edited =
            DocumentationRenderer.BeginMarker + "\r\n\ngenerated\n\n" + DocumentationRenderer.EndMarker;

        Assert.False(DocumentationRenderer.InsideMarkersModified(original, edited));
    }

    [Fact]
    public void InsideMarkersModified_TrueWhenInnerContentChanges()
    {
        var original =
            "HEADER\n" + DocumentationRenderer.BeginMarker + "\ngenerated\n" +
            DocumentationRenderer.EndMarker + "\nFOOTER\n";
        var edited =
            "HEADER\n" + DocumentationRenderer.BeginMarker + "\ngenerated EDITED BY USER\n" +
            DocumentationRenderer.EndMarker + "\nFOOTER\n";

        Assert.True(DocumentationRenderer.InsideMarkersModified(original, edited));
    }

    [Fact]
    public void InsideMarkersModified_TrueWhenMarkerBlockRemoved()
    {
        var original =
            DocumentationRenderer.BeginMarker + "\ngenerated\n" + DocumentationRenderer.EndMarker;
        var edited = "user deleted the markers entirely";

        Assert.True(DocumentationRenderer.InsideMarkersModified(original, edited));
    }

    [Fact]
    public void InsideMarkersModified_FalseWhenNeitherHasMarkers()
    {
        Assert.False(DocumentationRenderer.InsideMarkersModified("plain a", "plain b"));
    }

    // --- Helper -----------------------------------------------------------

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "telegrab_doc_tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CreateFile(string root, string relativePath)
    {
        var abs = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, "x");
    }

    private static void Cleanup(string root)
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }
}
