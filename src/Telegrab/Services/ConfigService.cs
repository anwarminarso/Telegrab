using System.IO;
using System.Text.Json;
using Telegrab.Models;

namespace Telegrab.Services;

/// <summary>
/// Membaca/menulis konfigurasi akun ke file JSON di folder data aplikasi.
/// Password tidak pernah disimpan. Session login ditangani terpisah oleh WTelegram
/// (file session.dat terenkripsi).
/// </summary>
public class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Cache di memori agar tidak baca+parse file JSON berulang kali (mis. saat batch download
    // memanggil Load() untuk setiap file). Lock reentrant (Monitor) aman untuk pemanggilan
    // bertingkat seperti SaveDownloadFolder -> Load/Save.
    private readonly object _gate = new();
    private AccountConfig? _cached;

    // State root download (logika murni, dapat diuji tanpa MAUI). ConfigService menambahkan
    // persistensi ke appsettings.json di atasnya.
    private readonly DownloadRootState _rootState = new();

    public string AppDataFolder { get; }
    public string ConfigPath { get; }
    public string SessionPath { get; }

    /// <summary>Folder download default jika user belum memilih (folder Downloads Windows).</summary>
    public string DefaultDownloadFolder { get; }

    /// <summary>Root download "strict" saat ini; null bila belum dikonfigurasi (Requirement 1).</summary>
    public string? DownloadRoot
    {
        get
        {
            Load(); // pastikan state terisi dari config tersimpan
            return _rootState.DownloadRoot;
        }
    }

    /// <summary>True bila root download sudah dikonfigurasi.</summary>
    public bool IsRootConfigured
    {
        get
        {
            Load();
            return _rootState.IsRootConfigured;
        }
    }

    /// <summary>Dipancarkan saat root download berubah (path baru). Lihat design.md.</summary>
    public event Action<string>? RootChanged
    {
        add => _rootState.RootChanged += value;
        remove => _rootState.RootChanged -= value;
    }

    public ConfigService()
    {
        //AppDataFolder = Path.Combine(FileSystem.AppDataDirectory, "Telegrab");
        AppDataFolder = FileSystem.AppDataDirectory;
        
        Directory.CreateDirectory(AppDataFolder);
        ConfigPath = Path.Combine(AppDataFolder, "appsettings.json");
        SessionPath = Path.Combine(AppDataFolder, "session.dat");
        DefaultDownloadFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "Telegrab");
    }

    public AccountConfig Load()
    {
        lock (_gate)
        {
            if (_cached != null) return _cached;

            AccountConfig cfg;
            if (!File.Exists(ConfigPath))
            {
                cfg = new AccountConfig();
            }
            else
            {
                try
                {
                    var json = File.ReadAllText(ConfigPath);
                    cfg = JsonSerializer.Deserialize<AccountConfig>(json) ?? new AccountConfig();
                }
                catch
                {
                    cfg = new AccountConfig();
                }
            }

            if (string.IsNullOrWhiteSpace(cfg.DownloadFolder))
                cfg.DownloadFolder = DefaultDownloadFolder;

            _cached = cfg;
            _rootState.Initialize(cfg.DownloadRoot); // sinkronkan state root (tanpa event)
            return _cached;
        }
    }

    public void Save(AccountConfig config)
    {
        lock (_gate)
        {
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ConfigPath, json);
            _cached = config; // jaga cache tetap sinkron dengan isi file
            _rootState.Initialize(config.DownloadRoot); // sinkronkan state (tanpa event)
        }
    }

    /// <summary>Perbarui hanya folder download tanpa mengubah kredensial lain.</summary>
    public void SaveDownloadFolder(string folder)
    {
        lock (_gate)
        {
            var cfg = Load();
            cfg.DownloadFolder = folder;
            Save(cfg);
        }
    }

    /// <summary>Apakah session login sebelumnya masih tersimpan.</summary>
    public bool HasSession() => File.Exists(SessionPath);

    /// <summary>
    /// Validasi kandidat root: pastikan folder ada/terbuat dan dapat ditulisi. Mendelegasikan
    /// ke <see cref="RootValidator"/> (logika murni). Lihat Requirement 1.1/3.1/3.2/3.3.
    /// </summary>
    public RootValidationResult ValidateRoot(string path) => RootValidator.Validate(path);

    /// <summary>
    /// Simpan root download ke konfigurasi lalu pancarkan <see cref="RootChanged"/>.
    /// Tidak melakukan validasi di sini; pemanggil sebaiknya memanggil
    /// <see cref="ValidateRoot"/> terlebih dahulu.
    /// </summary>
    public void SetDownloadRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Root path tidak boleh kosong.", nameof(path));

        lock (_gate)
        {
            var cfg = Load();
            cfg.DownloadRoot = path;
            Save(cfg);
        }

        // Pancarkan event di luar lock untuk menghindari deadlock pada handler.
        _rootState.Set(path);
    }
}
