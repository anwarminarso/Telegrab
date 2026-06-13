namespace Telegrab.Services;

/// <summary>
/// Menyelaraskan lifecycle koneksi <see cref="ManifestDbService"/> dengan root download
/// yang dikelola <see cref="ConfigService"/> (lihat design.md "Lifecycle koneksi DB").
///
/// Tanggung jawab:
/// - Saat startup (<see cref="Initialize"/>): bila root sudah terkonfigurasi DAN valid →
///   buka DB di root tersebut (<see cref="ManifestDbService.OpenForRoot"/>).
/// - Berlangganan <see cref="ConfigService.RootChanged"/>: tutup koneksi DB lama lalu
///   buka/buat DB di root baru (Requirement 2.4).
///
/// Strict gating (Requirement 1.2/3.2/3.3): bila root belum valid, DB dibiarkan tertutup
/// sehingga operasi unduh diblokir di tempat lain.
/// </summary>
public sealed class DbLifecycleCoordinator
{
    private readonly ConfigService _config;
    private readonly ManifestDbService _db;

    /// <summary>
    /// Dipancarkan SETELAH koneksi DB selesai dialihkan ke root baru (DB sudah ditutup lalu
    /// dibuka kembali bila valid). Argumen = root baru.
    ///
    /// Tujuannya membuat urutan reaksi terhadap penggantian root menjadi EKSPLISIT (B4):
    /// konsumer UI (mis. <c>MainViewModel</c>) berlangganan event ini — bukan
    /// <see cref="ConfigService.RootChanged"/> mentah — sehingga dijamin DB sudah siap sebelum
    /// mereka memuat ulang, tanpa bergantung pada urutan langganan event.
    /// </summary>
    public event Action<string>? RootSwitched;

    public DbLifecycleCoordinator(ConfigService config, ManifestDbService db)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _db = db ?? throw new ArgumentNullException(nameof(db));

        // Koordinator ini adalah SATU-SATUNYA pelanggan RootChanged mentah. Konsumer lain memakai
        // RootSwitched agar urutan (tukar DB → reaksi UI) terjamin.
        _config.RootChanged += OnRootChanged;
    }

    /// <summary>
    /// Dipanggil sekali saat startup aplikasi. Bila root terkonfigurasi dan lolos validasi
    /// (folder ada/terbuat & dapat ditulisi), buka DB di root tersebut. Bila tidak valid,
    /// biarkan DB tertutup (strict gating).
    /// </summary>
    public void Initialize()
    {
        var root = _config.DownloadRoot;
        if (string.IsNullOrWhiteSpace(root))
            return; // belum dikonfigurasi → DB tetap tertutup

        OpenIfValid(root);
    }

    /// <summary>
    /// Saat root berubah: tutup koneksi DB lama, lalu buka/buat DB di root baru bila valid.
    /// Tiap root self-contained — tidak ada file unduhan yang dipindahkan (Requirement 2.4).
    /// </summary>
    private void OnRootChanged(string newRoot)
    {
        _db.Close();

        if (!string.IsNullOrWhiteSpace(newRoot))
            OpenIfValid(newRoot);

        // DB sudah ditukar (ditutup, lalu dibuka kembali bila valid). Baru kabari konsumer UI
        // agar mereka memuat ulang di atas DB yang sudah benar (B4).
        RootSwitched?.Invoke(newRoot);
    }

    private void OpenIfValid(string root)
    {
        var result = _config.ValidateRoot(root);
        if (result.IsValid)
            _db.OpenForRoot(root);
    }
}
