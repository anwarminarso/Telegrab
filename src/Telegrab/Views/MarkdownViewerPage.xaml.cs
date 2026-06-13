using System.IO;
using MauiIcons.Core;
using Telegrab.ViewModels;

namespace Telegrab.Views;

/// <summary>
/// Modal penampil Markdown (Fase 2, Requirement 10). Merender <c>README.md</c> chat/topik aktif
/// ke <c>WebView</c> via ViewModel, dan membuka tautan media (relatif/absolut) di aplikasi luar
/// lewat <see cref="Launcher"/> (Requirement 10.2) — pendekatan robust untuk WebView2 di Windows,
/// karena navigasi <c>file://</c> di dalam WebView2 sering diblokir.
///
/// Tautan media relatif di README di-resolve memakai tag <c>&lt;base&gt;</c> bertarget
/// virtual-host (<see cref="MarkdownViewerViewModel.MediaHostName"/>) yang dipetakan ke folder
/// dokumen lewat <c>SetVirtualHostNameToFolderMapping</c> (Windows). Ini membuat gambar/video
/// in-app termuat, sementara README di disk tetap memakai path relatif (portabel untuk penampil
/// eksternal).
/// </summary>
public partial class MarkdownViewerPage : ContentPage
{
    private readonly MarkdownViewerViewModel _vm;
    private bool _loaded;

    public MarkdownViewerPage(MarkdownViewerViewModel vm)
    {
        InitializeComponent();
        _ = new MauiIcon(); // workaround namespace url-style MauiIcons (lihat MainPage)
        _vm = vm;
        BindingContext = vm;

        vm.CloseRequested += OnCloseRequested;
    }

    /// <summary>
    /// Tetapkan folder yang akan ditampilkan sebelum modal di-push. <paramref name="relativeFolder"/>
    /// relatif terhadap <paramref name="root"/> (root download absolut aktif).
    /// </summary>
    public void Initialize(string relativeFolder, string root) => _vm.Initialize(relativeFolder, root);

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded) return;
        _loaded = true;
        await _vm.LoadAsync();
    }

    private async void OnCloseRequested()
    {
        _vm.CloseRequested -= OnCloseRequested;
        await Navigation.PopModalAsync();
    }

    /// <summary>
    /// Intersep navigasi tautan. Urutan penanganan:
    /// <list type="number">
    ///   <item>Pastikan virtual-host media terpetakan ke folder dokumen untuk WebView ini
    ///         (Windows) sebelum konten/aset dimuat.</item>
    ///   <item>Tautan media (host <see cref="MarkdownViewerViewModel.MediaHostName"/>) → buka
    ///         BERKAS aslinya di aplikasi default OS (terjemahkan kembali ke path).</item>
    ///   <item>Tautan eksternal lain (file/http/https/mailto) → buka apa adanya di aplikasi
    ///         default OS (Requirement 10.2).</item>
    /// </list>
    /// Pemuatan konten HTML awal (skema <c>data:</c>/<c>about:</c>) dibiarkan lewat.
    /// </summary>
    private async void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        var url = e.Url;
        if (string.IsNullOrEmpty(url))
            return;

#if WINDOWS
        // Saat navigasi konten awal (data:), CoreWebView2 sudah siap → pasang pemetaan host
        // media agar fetch gambar/video relatif berikutnya ter-resolve ke folder dokumen.
        TryMapMediaHost(sender);
#endif

        // Tautan media (virtual host) → buka berkas asli di aplikasi default OS.
        if (url.StartsWith(MarkdownViewerViewModel.MediaBaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            await OpenMediaAsync(url);
            return;
        }

        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            try
            {
                await Launcher.Default.OpenAsync(new Uri(url));
            }
            catch
            {
                /* abaikan kegagalan membuka tautan eksternal */
            }
        }
    }

    /// <summary>
    /// Terjemahkan URL virtual-host media (<c>https://telegrab.media/&lt;file&gt;</c>) kembali ke
    /// path berkas di dalam folder dokumen, lalu buka di aplikasi default OS.
    /// </summary>
    private async Task OpenMediaAsync(string url)
    {
        var folder = _vm.FolderAbsolute;
        if (string.IsNullOrEmpty(folder))
            return;

        try
        {
            var relative = url.Substring(MarkdownViewerViewModel.MediaBaseUrl.Length);

            // Buang query/fragment bila ada.
            var cut = relative.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0)
                relative = relative[..cut];

            relative = Uri.UnescapeDataString(relative).Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(folder, relative));

            if (File.Exists(full))
                await Launcher.Default.OpenAsync(new Uri(full));
        }
        catch
        {
            /* abaikan kegagalan membuka berkas media */
        }
    }

#if WINDOWS
    /// <summary>
    /// Pastikan virtual-host <see cref="MarkdownViewerViewModel.MediaHostName"/> dipetakan ke
    /// folder dokumen aktif pada WebView2 milik <paramref name="sender"/>. Idempoten — aman
    /// dipanggil pada setiap event navigasi.
    /// </summary>
    private void TryMapMediaHost(object? sender)
    {
        if (sender is not WebView webView)
            return;

        var folder = _vm.FolderAbsolute;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return;

        if (webView.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.WebView2 native &&
            native.CoreWebView2 is not null)
        {
            try
            {
                native.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    MarkdownViewerViewModel.MediaHostName,
                    folder,
                    Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
            }
            catch
            {
                /* abaikan kegagalan pemetaan host (mis. CoreWebView2 belum siap) */
            }
        }
    }
#endif
}
