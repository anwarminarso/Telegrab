using Telegrab.Models;

namespace Telegrab.Services;

/// <summary>
/// Membangkitkan proyeksi <c>README.md</c> per folder dari manifest SQLite (Requirement 8 &amp; 9).
///
/// Pembagian tanggung jawab:
/// <list type="bullet">
///   <item><see cref="DocumentationRenderer"/> — LOGIKA MURNI render/merge (string → string),
///         diuji deterministik tanpa IO.</item>
///   <item><see cref="DocumentationService"/> — pembungkus tipis: resolusi root, baca/tulis file,
///         dan DEBOUNCE per folder agar batch unduh tidak menulis README berkali-kali.</item>
/// </list>
///
/// Tipe ini tetap LOGIKA MURNI terhadap MAUI (hanya bergantung pada <see cref="ManifestDbService"/>,
/// <c>System.IO</c>, dan <c>System.Threading</c>), sehingga dapat dilink &amp; diuji di proyek test.
/// Debounce memakai timer dan SENGAJA dipisah dari jalur murni agar test menargetkan
/// <see cref="DocumentationRenderer"/> secara langsung.
/// </summary>
public sealed class DocumentationService : IDisposable
{
    private const string ReadmeFileName = "README.md";

    private readonly ManifestDbService _db;
    private readonly TimeSpan _debounceDelay;
    private readonly object _gate = new();
    private readonly Dictionary<string, Timer> _timers =
        new(StringComparer.OrdinalIgnoreCase);

    // Folder yang sedang disunting di penampil dokumentasi (refcount). Selama nilainya > 0,
    // regenerasi README ter-debounce (UpdateFolderAsync) DILEWATI agar tidak menimpa berkas di
    // bawah editor — mencegah race edit vs unduhan selesai (4b). Rebuild eksplisit (aksi
    // pengguna) tetap dijalankan.
    private readonly Dictionary<string, int> _editSuppressions =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    /// <summary>
    /// Buat service.
    /// </summary>
    /// <param name="db">Sumber kebenaran manifest (menyediakan root aktif &amp; <c>QueryFolder</c>).</param>
    /// <param name="debounceDelay">Jeda debounce per folder; default 1 detik.</param>
    public DocumentationService(ManifestDbService db, TimeSpan? debounceDelay = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _debounceDelay = debounceDelay ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// Jadwalkan regenerasi <c>README.md</c> untuk <paramref name="relativeFolder"/> (di-debounce).
    /// Panggilan berulang dalam jendela debounce hanya menghasilkan satu penulisan.
    /// </summary>
    public Task UpdateFolderAsync(string relativeFolder)
    {
        ArgumentNullException.ThrowIfNull(relativeFolder);

        lock (_gate)
        {
            if (_disposed)
                return Task.CompletedTask;

            var key = NormalizeKey(relativeFolder);

            // Folder sedang disunting: jangan jadwalkan penulisan ulang di bawah editor (4b).
            if (_editSuppressions.ContainsKey(key))
                return Task.CompletedTask;

            if (_timers.TryGetValue(key, out var existing))
                existing.Dispose();

            var timer = new Timer(
                _ => OnDebounceElapsed(key),
                state: null,
                dueTime: _debounceDelay,
                period: Timeout.InfiniteTimeSpan);

            _timers[key] = timer;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Regenerasi <c>README.md</c> untuk <paramref name="relativeFolder"/> SEGERA
    /// (dipakai aksi "Rebuild documentation"). Membatalkan debounce yang tertunda untuk folder ini.
    /// </summary>
    public Task RebuildFolderAsync(string relativeFolder)
    {
        ArgumentNullException.ThrowIfNull(relativeFolder);

        var key = NormalizeKey(relativeFolder);

        lock (_gate)
        {
            if (_timers.Remove(key, out var existing))
                existing.Dispose();
        }

        RegenerateFolder(key);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tandai sebuah folder sedang disunting di penampil dokumentasi. Selama ditandai, regenerasi
    /// README ter-debounce untuk folder itu DILEWATI (lihat <see cref="UpdateFolderAsync"/>), agar
    /// tidak menimpa berkas di bawah editor (4b). Berpasangan dengan <see cref="EndEdit"/> (refcount).
    /// </summary>
    public void BeginEdit(string relativeFolder)
    {
        ArgumentNullException.ThrowIfNull(relativeFolder);
        var key = NormalizeKey(relativeFolder);
        lock (_gate)
        {
            _editSuppressions.TryGetValue(key, out var n);
            _editSuppressions[key] = n + 1;
        }
    }

    /// <summary>Akhiri penandaan "sedang disunting" untuk sebuah folder (lihat <see cref="BeginEdit"/>).</summary>
    public void EndEdit(string relativeFolder)
    {
        ArgumentNullException.ThrowIfNull(relativeFolder);
        var key = NormalizeKey(relativeFolder);
        lock (_gate)
        {
            if (!_editSuppressions.TryGetValue(key, out var n))
                return;
            if (n <= 1)
                _editSuppressions.Remove(key);
            else
                _editSuppressions[key] = n - 1;
        }
    }

    private void OnDebounceElapsed(string key)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_timers.Remove(key, out var timer))
                timer.Dispose();
        }

        RegenerateFolder(key);
    }

    /// <summary>
    /// Inti IO: query DB untuk folder, baca README lama (bila ada), render melalui
    /// <see cref="DocumentationRenderer"/>, lalu tulis kembali.
    /// </summary>
    private void RegenerateFolder(string relativeFolder)
    {
        if (!_db.IsReady)
            return;

        var root = _db.Root;
        if (string.IsNullOrEmpty(root))
            return;

        IReadOnlyList<MediaRecord> records = _db.QueryFolder(relativeFolder);

        var folderAbs = ResolveFolder(root, relativeFolder);
        Directory.CreateDirectory(folderAbs);

        var readmePath = Path.Combine(folderAbs, ReadmeFileName);
        var existing = File.Exists(readmePath) ? File.ReadAllText(readmePath) : null;

        var content = DocumentationRenderer.Render(records, existing);
        File.WriteAllText(readmePath, content);
    }

    private static string ResolveFolder(string root, string relativeFolder)
    {
        var normalized = NormalizeKey(relativeFolder);
        if (normalized.Length == 0)
            return Path.GetFullPath(root);

        var os = normalized.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(root, os));
    }

    private static string NormalizeKey(string folder)
    {
        if (string.IsNullOrEmpty(folder))
            return string.Empty;
        var normalized = folder.Replace('\\', '/').Trim('/');
        return normalized == "." ? string.Empty : normalized;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var timer in _timers.Values)
                timer.Dispose();
            _timers.Clear();
        }
    }
}
