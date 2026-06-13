namespace Telegrab.Services;

/// <summary>
/// State MURNI dari root download aktif: menyimpan path saat ini, menentukan apakah
/// root sudah dikonfigurasi, dan memancarkan <see cref="RootChanged"/> saat root diganti.
///
/// Dipakai secara komposisi oleh <c>ConfigService</c> (yang menambahkan persistensi ke
/// <c>appsettings.json</c>). Karena kelas ini bebas dependensi MAUI, ia dapat dilink &
/// diuji langsung di proyek test (net10.0) — termasuk perilaku event <see cref="RootChanged"/>.
/// </summary>
public sealed class DownloadRootState
{
    private readonly object _gate = new();
    private string? _downloadRoot;

    /// <summary>Root download saat ini; null bila belum dikonfigurasi.</summary>
    public string? DownloadRoot
    {
        get { lock (_gate) return _downloadRoot; }
    }

    /// <summary>True bila root sudah dikonfigurasi (tidak kosong).</summary>
    public bool IsRootConfigured
    {
        get { lock (_gate) return !string.IsNullOrWhiteSpace(_downloadRoot); }
    }

    /// <summary>Dipancarkan saat root berubah; argumen = path root baru.</summary>
    public event Action<string>? RootChanged;

    /// <summary>
    /// Set root tanpa memancarkan event. Dipakai saat memuat nilai tersimpan dari config
    /// agar pelanggan tidak menerima notifikasi palsu pada startup.
    /// </summary>
    public void Initialize(string? path)
    {
        lock (_gate)
        {
            _downloadRoot = string.IsNullOrWhiteSpace(path) ? null : path;
        }
    }

    /// <summary>
    /// Set root baru dan pancarkan <see cref="RootChanged"/>. Event hanya dipancarkan bila
    /// nilai berubah dari sebelumnya.
    /// </summary>
    public void Set(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Root path tidak boleh kosong.", nameof(path));

        bool changed;
        lock (_gate)
        {
            changed = !string.Equals(_downloadRoot, path, StringComparison.Ordinal);
            _downloadRoot = path;
        }

        if (changed)
            RootChanged?.Invoke(path);
    }
}
