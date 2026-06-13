using System.Globalization;
using System.Text;
using Telegrab.Models;

namespace Telegrab.Services;

/// <summary>
/// LOGIKA MURNI untuk merender &amp; menggabungkan proyeksi <c>README.md</c> (Requirement 8).
///
/// Bagian ini sengaja dipisah dari <see cref="DocumentationService"/> agar dapat diuji
/// secara DETERMINISTIK: fungsi inti <see cref="Render"/> adalah transformasi
/// <c>string -&gt; string</c> (daftar <see cref="MediaRecord"/> + isi file lama → isi file baru),
/// TANPA IO, timer/debounce, maupun dependensi MAUI.
///
/// Aturan render (lihat design.md "Render README.md"):
/// <list type="bullet">
///   <item>Konten ter-generate dibungkus di antara <see cref="BeginMarker"/> dan
///         <see cref="EndMarker"/>; teks di luar penanda dipertahankan apa adanya.</item>
///   <item>Bila file/penanda tidak ada → tulis ulang file lengkap dengan blok penanda baru.</item>
///   <item>Media dikelompokkan per-post (album ber-<c>group_id</c> sama → satu grup).</item>
///   <item>Caption/note di-escape: <c>|</c> → <c>\|</c>, newline → spasi.</item>
///   <item>Tanggal memakai current culture + waktu lokal (DB menyimpan UTC).</item>
///   <item>Renderer merender PERSIS record yang diberikan (pemfilteran <c>File.Exists</c>
///         dilakukan hulu oleh <c>QueryFolder</c>).</item>
/// </list>
/// </summary>
public static class DocumentationRenderer
{
    /// <summary>Penanda awal blok ter-generate.</summary>
    public const string BeginMarker = "<!-- TELEGRAB:BEGIN -->";

    /// <summary>Penanda akhir blok ter-generate.</summary>
    public const string EndMarker = "<!-- TELEGRAB:END -->";

    private const string Newline = "\n";

    /// <summary>
    /// Hasilkan isi <c>README.md</c> baru dari <paramref name="records"/> dan isi file lama
    /// <paramref name="existingContent"/>.
    ///
    /// Bila <paramref name="existingContent"/> memuat sepasang penanda yang valid → hanya
    /// bagian DALAM penanda yang diganti, teks di luar dipertahankan. Selain itu (file/penanda
    /// hilang) → file ditulis ulang lengkap dengan blok penanda baru.
    /// </summary>
    /// <param name="records">Media yang akan dirender (sudah terurut &amp; terfilter dari DB).</param>
    /// <param name="existingContent">Isi README.md saat ini, atau <c>null</c> bila belum ada.</param>
    /// <param name="culture">Culture untuk format tanggal; default <see cref="CultureInfo.CurrentCulture"/>.</param>
    /// <param name="toLocalTime">
    /// Konverter UTC → waktu tampil; default <see cref="DateTime.ToLocalTime()"/>.
    /// Diparametrikan agar test bisa deterministik.
    /// </param>
    public static string Render(
        IReadOnlyList<MediaRecord> records,
        string? existingContent,
        CultureInfo? culture = null,
        Func<DateTime, DateTime>? toLocalTime = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        culture ??= CultureInfo.CurrentCulture;
        toLocalTime ??= static utc => utc.ToLocalTime();

        var generated = BuildGeneratedBlock(records, culture, toLocalTime);
        return MergeIntoMarkers(existingContent, generated);
    }

    /// <summary>
    /// Bangun isi blok ter-generate (TANPA penanda) dari record yang sudah terurut.
    /// Media dikelompokkan per-post mempertahankan urutan kemunculan pertama tiap grup.
    /// </summary>
    public static string BuildGeneratedBlock(
        IReadOnlyList<MediaRecord> records,
        CultureInfo? culture = null,
        Func<DateTime, DateTime>? toLocalTime = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        culture ??= CultureInfo.CurrentCulture;
        toLocalTime ??= static utc => utc.ToLocalTime();

        var groups = GroupPerPost(records);
        var sb = new StringBuilder();

        for (int i = 0; i < groups.Count; i++)
        {
            if (i > 0)
                sb.Append(Newline);
            AppendPost(sb, groups[i], culture, toLocalTime);
        }

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Gabungkan <paramref name="generated"/> ke dalam penanda di <paramref name="existingContent"/>.
    /// Jika penanda valid ditemukan → ganti hanya bagian dalam (pertahankan luar). Selain itu →
    /// kembalikan file lengkap berisi blok penanda baru saja.
    /// </summary>
    public static string MergeIntoMarkers(string? existingContent, string generated)
    {
        var block = BeginMarker + Newline + generated + Newline + EndMarker;

        if (!string.IsNullOrEmpty(existingContent))
        {
            int begin = existingContent.IndexOf(BeginMarker, StringComparison.Ordinal);
            int end = begin < 0
                ? -1
                : existingContent.IndexOf(EndMarker, begin + BeginMarker.Length, StringComparison.Ordinal);

            if (begin >= 0 && end > begin)
            {
                var before = existingContent[..begin];
                var after = existingContent[(end + EndMarker.Length)..];
                return before + block + after;
            }
        }

        // Fallback: file/penanda hilang → tulis ulang file lengkap dengan blok penanda baru.
        return block + Newline;
    }

    /// <summary>
    /// Ekstrak teks DI DALAM blok penanda dari <paramref name="content"/> (tanpa penanda itu
    /// sendiri). Mengembalikan <c>false</c> bila pasangan penanda valid tidak ditemukan
    /// (mis. file tanpa penanda, atau hanya salah satu penanda hadir).
    ///
    /// LOGIKA MURNI — dipakai editor (Fase 3) untuk mendeteksi suntingan di dalam blok
    /// ter-generate (Requirement 11.4).
    /// </summary>
    public static bool TryGetInsideMarkers(string? content, out string inside)
    {
        inside = string.Empty;
        if (string.IsNullOrEmpty(content))
            return false;

        int begin = content.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (begin < 0)
            return false;

        int innerStart = begin + BeginMarker.Length;
        int end = content.IndexOf(EndMarker, innerStart, StringComparison.Ordinal);
        if (end < innerStart)
            return false;

        inside = content[innerStart..end];
        return true;
    }

    /// <summary>
    /// Tentukan apakah isi DI DALAM blok penanda berubah antara <paramref name="original"/> dan
    /// <paramref name="edited"/> (Requirement 11.4). Perbandingan menormalkan akhir-baris dan
    /// memangkas whitespace tepi agar perbedaan sepele (mis. CRLF vs LF, baris kosong di tepi)
    /// tidak dianggap perubahan.
    ///
    /// Perilaku:
    /// <list type="bullet">
    ///   <item>Keduanya tanpa blok penanda → <c>false</c> (tak ada blok ter-generate untuk ditimpa).</item>
    ///   <item>Salah satu punya blok, yang lain tidak (blok ditambah/dihapus) → <c>true</c>.</item>
    ///   <item>Keduanya punya blok → bandingkan isi dalamnya yang dinormalkan.</item>
    /// </list>
    /// </summary>
    public static bool InsideMarkersModified(string? original, string? edited)
    {
        var hasOriginal = TryGetInsideMarkers(original, out var originalInside);
        var hasEdited = TryGetInsideMarkers(edited, out var editedInside);

        if (!hasOriginal && !hasEdited)
            return false;
        if (hasOriginal != hasEdited)
            return true;

        return !string.Equals(
            NormalizeRegion(originalInside),
            NormalizeRegion(editedInside),
            StringComparison.Ordinal);
    }

    private static string NormalizeRegion(string region) =>
        region.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n', ' ', '\t');

    // --- Internal ----------------------------------------------------------

    /// <summary>
    /// Kelompokkan record per-post: anggota album (<c>GroupId</c> sama &amp; bukan 0) menjadi
    /// satu grup; record non-album menjadi grup tunggal. Urutan grup mengikuti kemunculan
    /// pertama (record sudah terurut dari <c>QueryFolder</c>).
    /// </summary>
    private static List<List<MediaRecord>> GroupPerPost(IReadOnlyList<MediaRecord> records)
    {
        var groups = new List<List<MediaRecord>>();
        var albumIndex = new Dictionary<long, int>();

        foreach (var record in records)
        {
            if (record.GroupId is { } gid && gid != 0)
            {
                if (albumIndex.TryGetValue(gid, out var idx))
                {
                    groups[idx].Add(record);
                }
                else
                {
                    albumIndex[gid] = groups.Count;
                    groups.Add(new List<MediaRecord> { record });
                }
            }
            else
            {
                groups.Add(new List<MediaRecord> { record });
            }
        }

        return groups;
    }

    private static void AppendPost(
        StringBuilder sb,
        List<MediaRecord> group,
        CultureInfo culture,
        Func<DateTime, DateTime> toLocalTime)
    {
        var head = group[0];

        // Heading: ## {tanggal current-culture + waktu lokal} · {sender}
        var localDate = toLocalTime(EnsureUtc(head.MessageDateUtc));
        var dateText = localDate.ToString("g", culture);
        var sender = string.IsNullOrWhiteSpace(head.Sender) ? "—" : head.Sender!.Trim();
        sb.Append("## ").Append(dateText).Append(" · ").Append(sender).Append(Newline);

        // Caption (atau placeholder) — dipakai dari record kepala (album berbagi caption).
        var caption = Normalize(head.Caption);
        sb.Append(string.IsNullOrEmpty(caption) ? "_(tanpa deskripsi)_" : caption).Append(Newline);

        // Label sumber bila relevan.
        var label = SourceLabel(head.CaptionSource, caption);
        if (label is not null)
            sb.Append(label).Append(Newline);

        // Baris kosong sebelum daftar file.
        sb.Append(Newline);

        // Daftar file dalam grup.
        foreach (var media in group)
        {
            var fileName = media.FileName ?? string.Empty;
            var link = BuildRelativeLink(fileName);
            sb.Append("- [").Append(EscapeInline(fileName)).Append("](").Append(link).Append(')')
              .Append(" — ").Append(media.Type).Append(" · ").Append(FormatSize(media.Size))
              .Append(Newline);
        }

        // Catatan tambahan (case 8) bila ada.
        var note = Normalize(head.Note);
        if (!string.IsNullOrEmpty(note))
            sb.Append("> catatan: ").Append(note).Append(Newline);
    }

    private static string? SourceLabel(CaptionSource source, string caption)
    {
        // Label hanya relevan bila ada caption nyata.
        if (string.IsNullOrEmpty(caption))
            return null;

        return source switch
        {
            CaptionSource.Reply => "_(membalas)_",
            CaptionSource.Inferred => "_(inferred)_",
            _ => null,
        };
    }

    /// <summary>Escape karakter perusak layout Markdown pada caption/note: newline → spasi, <c>|</c> → <c>\|</c>.</summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return EscapeInline(value);
    }

    private static string EscapeInline(string value)
    {
        // Newline (CRLF/CR/LF) → spasi tunggal, lalu pipe → \|.
        var collapsed = value
            .Replace("\r\n", " ")
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace("|", "\\|");
        return collapsed.Trim();
    }

    private static string BuildRelativeLink(string fileName)
    {
        // Tautan relatif di dalam folder yang sama: cukup nama file, dengan spasi di-encode.
        return Uri.EscapeDataString(fileName).Replace("%2F", "/");
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 0)
            bytes = 0;

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[unit]}"
            : string.Format(CultureInfo.InvariantCulture, "{0:0.#} {1}", size, units[unit]);
    }

    private static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
