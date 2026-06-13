using MauiIcons.Core;
using Telegrab.ViewModels;

namespace Telegrab.Views;

/// <summary>
/// Modal penampil Markdown (Fase 2, Requirement 10). Merender <c>README.md</c> chat/topik aktif
/// ke <c>WebView</c> via ViewModel, dan membuka tautan media (relatif/absolut) di aplikasi luar
/// lewat <see cref="Launcher"/> (Requirement 10.2) — pendekatan robust untuk WebView2 di Windows,
/// karena navigasi <c>file://</c> di dalam WebView2 sering diblokir.
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
    /// Intersep navigasi tautan: untuk skema file/http(s)/mailto, batalkan navigasi dalam WebView
    /// dan buka di aplikasi default OS (Requirement 10.2). Pemuatan konten HTML awal (skema
    /// data:/about:) dibiarkan lewat. <c>&lt;base&gt;</c> di dokumen membuat tautan media relatif
    /// ter-resolve menjadi URI <c>file://</c> absolut sebelum sampai ke sini.
    /// </summary>
    private async void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        var url = e.Url;
        if (string.IsNullOrEmpty(url))
            return;

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
}
