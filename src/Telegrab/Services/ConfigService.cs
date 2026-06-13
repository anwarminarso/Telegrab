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

    public string AppDataFolder { get; }
    public string ConfigPath { get; }
    public string SessionPath { get; }

    /// <summary>Folder download default jika user belum memilih (folder Downloads Windows).</summary>
    public string DefaultDownloadFolder { get; }

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
}
