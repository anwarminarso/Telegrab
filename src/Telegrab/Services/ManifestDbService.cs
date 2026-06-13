using Microsoft.Data.Sqlite;
using Telegrab.Models;

namespace Telegrab.Services;

/// <summary>
/// Sumber kebenaran manifest unduhan berbasis SQLite (<c>telegrab.db</c> di root download).
///
/// Tipe LOGIKA MURNI — hanya bergantung pada <c>Microsoft.Data.Sqlite</c> dan model POCO
/// (<see cref="MediaRecord"/>, <see cref="CaptionSource"/>). TANPA dependensi MAUI/WTelegram
/// agar bisa dilink langsung ke proyek test.
///
/// Lifecycle: satu koneksi long-lived ke <c>{root}/telegrab.db</c> (lihat <see cref="OpenForRoot"/>),
/// memakai mode WAL dan <c>lock</c> untuk menserialkan operasi (worker antrian unduh sekuensial).
/// </summary>
public sealed class ManifestDbService : IDisposable
{
    private const string DbFileName = "telegrab.db";

    /// <summary>Format tanggal UTC ISO 8601 yang dapat diurutkan secara leksikografis.</summary>
    private const string UtcFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    private readonly object _gate = new();

    private SqliteConnection? _connection;
    private string? _root;

    /// <summary>True bila koneksi DB terbuka untuk root aktif.</summary>
    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _connection is not null
                    && _connection.State == System.Data.ConnectionState.Open
                    && _root is not null;
            }
        }
    }

    /// <summary>Root aktif (absolut) bila DB terbuka; null bila belum dibuka.</summary>
    public string? Root
    {
        get { lock (_gate) { return _root; } }
    }

    /// <summary>
    /// Tutup koneksi lama (bila ada), buka/buat <c>telegrab.db</c> di root baru, lalu
    /// jalankan <see cref="EnsureSchema"/>. Bila DB sudah ada (root lama), skema tetap ada
    /// sehingga status "sudah diunduh" otomatis pulih.
    /// </summary>
    public void OpenForRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Root download tidak boleh kosong.", nameof(root));

        lock (_gate)
        {
            CloseInternal();

            var fullRoot = Path.GetFullPath(root);
            Directory.CreateDirectory(fullRoot);

            var dbPath = Path.Combine(fullRoot, DbFileName);
            var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();

            _connection = connection;
            _root = fullRoot;

            EnsureSchemaInternal();
        }
    }

    /// <summary>Tutup koneksi DB aktif (idempotent).</summary>
    public void Close()
    {
        lock (_gate)
        {
            CloseInternal();
        }
    }

    /// <summary>
    /// Buat skema tabel <c>media</c> + index bila belum ada, dan aktifkan mode WAL.
    /// Aman dipanggil berulang.
    /// </summary>
    public void EnsureSchema()
    {
        lock (_gate)
        {
            EnsureSchemaInternal();
        }
    }

    /// <summary>
    /// Periksa apakah media (<paramref name="chatId"/>, <paramref name="messageId"/>,
    /// <paramref name="mediaId"/>) sudah tercatat DAN file fisiknya masih ada di disk.
    /// Path relatif di-resolve menjadi absolut terhadap root aktif lalu dicek <c>File.Exists</c>.
    /// </summary>
    public bool IsDownloaded(long chatId, int messageId, long mediaId, out string absolutePath)
    {
        lock (_gate)
        {
            absolutePath = string.Empty;
            var connection = RequireConnection();

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT relative_path FROM media " +
                "WHERE chat_id = $chatId AND message_id = $messageId AND media_id = $mediaId " +
                "LIMIT 1;";
            command.Parameters.AddWithValue("$chatId", chatId);
            command.Parameters.AddWithValue("$messageId", messageId);
            command.Parameters.AddWithValue("$mediaId", mediaId);

            var relativePath = command.ExecuteScalar() as string;
            if (string.IsNullOrEmpty(relativePath))
                return false;

            absolutePath = ResolveAbsolute(relativePath);
            return File.Exists(absolutePath);
        }
    }

    /// <summary>
    /// Upsert satu baris ke tabel <c>media</c> berdasarkan kunci
    /// (<c>chat_id</c>, <c>message_id</c>, <c>media_id</c>). Pencatatan ulang kunci yang sama
    /// hanya memperbarui baris yang ada, tidak menggandakan (idempotensi manifest).
    /// </summary>
    public void Mark(MediaRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_gate)
        {
            var connection = RequireConnection();

            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO media (" +
                " chat_id, message_id, media_id, group_id, chat_title, topic_title," +
                " relative_path, file_name, size, type, width, height, duration_seconds," +
                " sender, caption, caption_source, caption_from_message_id, note," +
                " note_from_message_id, message_date_utc, downloaded_at_utc" +
                ") VALUES (" +
                " $chatId, $messageId, $mediaId, $groupId, $chatTitle, $topicTitle," +
                " $relativePath, $fileName, $size, $type, $width, $height, $durationSeconds," +
                " $sender, $caption, $captionSource, $captionFromMessageId, $note," +
                " $noteFromMessageId, $messageDateUtc, $downloadedAtUtc" +
                ") ON CONFLICT(chat_id, message_id, media_id) DO UPDATE SET" +
                " group_id = excluded.group_id," +
                " chat_title = excluded.chat_title," +
                " topic_title = excluded.topic_title," +
                " relative_path = excluded.relative_path," +
                " file_name = excluded.file_name," +
                " size = excluded.size," +
                " type = excluded.type," +
                " width = excluded.width," +
                " height = excluded.height," +
                " duration_seconds = excluded.duration_seconds," +
                " sender = excluded.sender," +
                " caption = excluded.caption," +
                " caption_source = excluded.caption_source," +
                " caption_from_message_id = excluded.caption_from_message_id," +
                " note = excluded.note," +
                " note_from_message_id = excluded.note_from_message_id," +
                " message_date_utc = excluded.message_date_utc," +
                " downloaded_at_utc = excluded.downloaded_at_utc;";

            command.Parameters.AddWithValue("$chatId", record.ChatId);
            command.Parameters.AddWithValue("$messageId", record.MessageId);
            command.Parameters.AddWithValue("$mediaId", record.MediaId);
            command.Parameters.AddWithValue("$groupId", (object?)record.GroupId ?? DBNull.Value);
            command.Parameters.AddWithValue("$chatTitle", (object?)record.ChatTitle ?? DBNull.Value);
            command.Parameters.AddWithValue("$topicTitle", (object?)record.TopicTitle ?? DBNull.Value);
            command.Parameters.AddWithValue("$relativePath", NormalizeRelative(record.RelativePath));
            command.Parameters.AddWithValue("$fileName", record.FileName ?? string.Empty);
            command.Parameters.AddWithValue("$size", record.Size);
            command.Parameters.AddWithValue("$type", record.Type ?? string.Empty);
            command.Parameters.AddWithValue("$width", (object?)record.Width ?? DBNull.Value);
            command.Parameters.AddWithValue("$height", (object?)record.Height ?? DBNull.Value);
            command.Parameters.AddWithValue("$durationSeconds", (object?)record.DurationSeconds ?? DBNull.Value);
            command.Parameters.AddWithValue("$sender", (object?)record.Sender ?? DBNull.Value);
            command.Parameters.AddWithValue("$caption", (object?)record.Caption ?? DBNull.Value);
            command.Parameters.AddWithValue("$captionSource", ToText(record.CaptionSource));
            command.Parameters.AddWithValue("$captionFromMessageId", (object?)record.CaptionFromMessageId ?? DBNull.Value);
            command.Parameters.AddWithValue("$note", (object?)record.Note ?? DBNull.Value);
            command.Parameters.AddWithValue("$noteFromMessageId", (object?)record.NoteFromMessageId ?? DBNull.Value);
            command.Parameters.AddWithValue("$messageDateUtc", FormatUtc(record.MessageDateUtc));
            command.Parameters.AddWithValue("$downloadedAtUtc", FormatUtc(record.DownloadedAtUtc));

            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Kembalikan media yang berada LANGSUNG di dalam <paramref name="relativeFolder"/>
    /// (anak langsung, bukan rekursif), difilter hanya yang <c>File.Exists</c>-nya true,
    /// terurut <c>message_date_utc</c>, lalu <c>message_id</c>, lalu <c>media_id</c>.
    /// </summary>
    public IReadOnlyList<MediaRecord> QueryFolder(string relativeFolder)
    {
        lock (_gate)
        {
            var connection = RequireConnection();
            var folder = NormalizeFolder(relativeFolder);

            using var command = connection.CreateCommand();
            if (folder.Length == 0)
            {
                command.CommandText =
                    "SELECT * FROM media ORDER BY message_date_utc, message_id, media_id;";
            }
            else
            {
                command.CommandText =
                    "SELECT * FROM media WHERE relative_path LIKE $prefix ESCAPE '\\' " +
                    "ORDER BY message_date_utc, message_id, media_id;";
                command.Parameters.AddWithValue("$prefix", EscapeLike(folder) + "/%");
            }

            var results = new List<MediaRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var record = ReadRecord(reader);

                // Anak langsung: direktori dari relative_path harus persis == folder yang diminta.
                var parent = NormalizeFolder(GetDirectory(record.RelativePath));
                if (!string.Equals(parent, folder, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Konsistensi disk↔dok: hanya file yang masih ada.
                if (!File.Exists(ResolveAbsolute(record.RelativePath)))
                    continue;

                results.Add(record);
            }

            return results;
        }
    }

    // --- Helper internal ---------------------------------------------------

    private SqliteConnection RequireConnection()
    {
        if (_connection is null || _connection.State != System.Data.ConnectionState.Open || _root is null)
            throw new InvalidOperationException(
                "ManifestDbService belum siap. Panggil OpenForRoot(root) terlebih dahulu.");
        return _connection;
    }

    private void EnsureSchemaInternal()
    {
        var connection = RequireConnection();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS media (" +
            " chat_id                 INTEGER NOT NULL," +
            " message_id              INTEGER NOT NULL," +
            " media_id                INTEGER NOT NULL," +
            " group_id                INTEGER," +
            " chat_title              TEXT," +
            " topic_title             TEXT," +
            " relative_path           TEXT NOT NULL," +
            " file_name               TEXT NOT NULL," +
            " size                    INTEGER NOT NULL DEFAULT 0," +
            " type                    TEXT NOT NULL," +
            " width                   INTEGER," +
            " height                  INTEGER," +
            " duration_seconds        REAL," +
            " sender                  TEXT," +
            " caption                 TEXT," +
            " caption_source          TEXT NOT NULL DEFAULT 'none'," +
            " caption_from_message_id INTEGER," +
            " note                    TEXT," +
            " note_from_message_id    INTEGER," +
            " message_date_utc        TEXT NOT NULL," +
            " downloaded_at_utc       TEXT NOT NULL," +
            " PRIMARY KEY (chat_id, message_id, media_id)" +
            ");" +
            "CREATE INDEX IF NOT EXISTS ix_media_folder ON media (relative_path);" +
            "CREATE INDEX IF NOT EXISTS ix_media_chat   ON media (chat_id);";
        command.ExecuteNonQuery();
    }

    private void CloseInternal()
    {
        if (_connection is not null)
        {
            try
            {
                _connection.Close();
                _connection.Dispose();
            }
            finally
            {
                _connection = null;
                _root = null;
                // Bersihkan pool agar file DB tidak terkunci (penting untuk ganti root / cleanup test).
                SqliteConnection.ClearAllPools();
            }
        }
    }

    private string ResolveAbsolute(string relativePath)
    {
        var root = _root ?? throw new InvalidOperationException("Root belum diset.");
        return Path.GetFullPath(Path.Combine(root, NormalizeToOsSeparator(relativePath)));
    }

    private static MediaRecord ReadRecord(SqliteDataReader reader)
    {
        return new MediaRecord
        {
            ChatId = reader.GetInt64(reader.GetOrdinal("chat_id")),
            MessageId = reader.GetInt32(reader.GetOrdinal("message_id")),
            MediaId = reader.GetInt64(reader.GetOrdinal("media_id")),
            GroupId = GetNullableInt64(reader, "group_id"),
            ChatTitle = GetNullableString(reader, "chat_title"),
            TopicTitle = GetNullableString(reader, "topic_title"),
            RelativePath = reader.GetString(reader.GetOrdinal("relative_path")),
            FileName = reader.GetString(reader.GetOrdinal("file_name")),
            Size = reader.GetInt64(reader.GetOrdinal("size")),
            Type = reader.GetString(reader.GetOrdinal("type")),
            Width = GetNullableInt32(reader, "width"),
            Height = GetNullableInt32(reader, "height"),
            DurationSeconds = GetNullableDouble(reader, "duration_seconds"),
            Sender = GetNullableString(reader, "sender"),
            Caption = GetNullableString(reader, "caption"),
            CaptionSource = FromText(GetNullableString(reader, "caption_source")),
            CaptionFromMessageId = GetNullableInt32(reader, "caption_from_message_id"),
            Note = GetNullableString(reader, "note"),
            NoteFromMessageId = GetNullableInt32(reader, "note_from_message_id"),
            MessageDateUtc = ParseUtc(reader.GetString(reader.GetOrdinal("message_date_utc"))),
            DownloadedAtUtc = ParseUtc(reader.GetString(reader.GetOrdinal("downloaded_at_utc"))),
        };
    }

    private static long? GetNullableInt64(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static int? GetNullableInt32(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static double? GetNullableDouble(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static string ToText(CaptionSource source) => source switch
    {
        CaptionSource.Own => "own",
        CaptionSource.Album => "album",
        CaptionSource.Reply => "reply",
        CaptionSource.Inferred => "inferred",
        _ => "none",
    };

    private static CaptionSource FromText(string? text) => text switch
    {
        "own" => CaptionSource.Own,
        "album" => CaptionSource.Album,
        "reply" => CaptionSource.Reply,
        "inferred" => CaptionSource.Inferred,
        _ => CaptionSource.None,
    };

    private static string FormatUtc(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        return utc.ToString(UtcFormat, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static DateTime ParseUtc(string text)
    {
        return DateTime.Parse(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
    }

    /// <summary>Normalkan path relatif untuk penyimpanan DB: pakai pemisah '/' dan tanpa leading/trailing slash.</summary>
    private static string NormalizeRelative(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return string.Empty;
        return relativePath.Replace('\\', '/').Trim('/');
    }

    /// <summary>Normalkan nama folder relatif untuk pembandingan (lowercase separator, tanpa slash tepi).</summary>
    private static string NormalizeFolder(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
            return string.Empty;
        var normalized = folder.Replace('\\', '/').Trim('/');
        return normalized == "." ? string.Empty : normalized;
    }

    private static string GetDirectory(string relativePath)
    {
        var normalized = NormalizeRelative(relativePath);
        var idx = normalized.LastIndexOf('/');
        return idx < 0 ? string.Empty : normalized[..idx];
    }

    private static string NormalizeToOsSeparator(string relativePath)
    {
        var normalized = NormalizeRelative(relativePath);
        return normalized.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string EscapeLike(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
    }

    public void Dispose() => Close();
}
