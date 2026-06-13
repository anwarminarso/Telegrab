using CommunityToolkit.Maui.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Telegrab.Models;
using Telegrab.Services;

namespace Telegrab.ViewModels;

/// <summary>
/// ViewModel modal konfigurasi root download (Requirement 2). Menampilkan root saat ini,
/// memungkinkan pengguna mengganti folder lewat <see cref="FolderPicker"/>, memvalidasi izin
/// (Requirement 3), lalu menyimpannya sebagai root strict (Requirement 2.3).
///
/// Membatalkan modal (tombol Close) TIDAK mengubah konfigurasi (Requirement 2.5): perubahan
/// hanya terjadi saat folder valid dipilih dan <see cref="ConfigService.SetDownloadRoot"/>
/// dipanggil.
/// </summary>
public partial class ConfigViewModel : ObservableObject
{
    private readonly ConfigService _config;

    [ObservableProperty] private string _currentRoot = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>True bila status menggambarkan kondisi error/perlu tindakan (untuk pewarnaan UI).</summary>
    [ObservableProperty] private bool _hasError;

    /// <summary>Diminta saat modal perlu ditutup (root berhasil disimpan atau dibatalkan).</summary>
    public event Action? CloseRequested;

    public ConfigViewModel(ConfigService config)
    {
        _config = config;
        RefreshState();
    }

    /// <summary>Sinkronkan tampilan dengan keadaan root saat ini di <see cref="ConfigService"/>.</summary>
    private void RefreshState()
    {
        var root = _config.DownloadRoot;
        CurrentRoot = string.IsNullOrWhiteSpace(root) ? "(belum dikonfigurasi)" : root;

        if (_config.IsRootConfigured)
        {
            StatusText = "Folder download aktif. Unduhan akan disimpan di sini.";
            HasError = false;
        }
        else
        {
            StatusText = "Folder download belum dikonfigurasi. Pilih folder untuk mengaktifkan unduhan.";
            HasError = true;
        }
    }

    /// <summary>
    /// Pilih folder baru lewat <see cref="FolderPicker"/>. Bila pemilihan dibatalkan, konfigurasi
    /// dibiarkan apa adanya (Requirement 2.5). Bila folder dipilih → validasi izin
    /// (Requirement 3); valid → simpan sebagai root lalu tutup modal; tidak valid → tampilkan
    /// pesan yang membedakan folder tidak dapat dibuat vs tidak dapat ditulisi.
    /// </summary>
    [RelayCommand]
    private async Task ChangeFolderAsync()
    {
        try
        {
            var result = await FolderPicker.Default.PickAsync(CancellationToken.None);
            if (!result.IsSuccessful || result.Folder is null)
                return; // dibatalkan: konfigurasi tetap (Requirement 2.5)

            var path = result.Folder.Path;
            var validation = _config.ValidateRoot(path);
            if (!validation.IsValid)
            {
                HasError = true;
                StatusText = BuildErrorMessage(validation);
                return;
            }

            // Valid → simpan sebagai root strict; DbLifecycleCoordinator (langganan RootChanged)
            // akan menutup DB lama & membuka DB di root baru secara otomatis (Requirement 2.4).
            _config.SetDownloadRoot(path);
            RefreshState();
            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusText = $"Gagal memilih folder: {ex.Message}";
        }
    }

    /// <summary>Tutup modal tanpa mengubah konfigurasi (Requirement 2.5).</summary>
    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    /// <summary>
    /// Bangun pesan error yang membedakan jenis kegagalan validasi root: folder tidak dapat
    /// dibuat (<see cref="RootValidationResult.CannotCreate"/>) vs tidak dapat ditulisi
    /// (<see cref="RootValidationResult.NotWritable"/>).
    /// </summary>
    private static string BuildErrorMessage(RootValidationResult result)
    {
        if (result.CannotCreate)
            return $"Folder tidak dapat dibuat: {result.Error}";
        if (result.NotWritable)
            return $"Folder tidak dapat ditulisi (periksa izin akses): {result.Error}";
        return result.Error ?? "Folder yang dipilih tidak valid.";
    }
}
