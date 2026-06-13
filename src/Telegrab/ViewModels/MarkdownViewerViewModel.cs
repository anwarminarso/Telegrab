using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Telegrab.Services;

namespace Telegrab.ViewModels;

/// <summary>
/// ViewModel penampil Markdown (Fase 2, Requirement 10). Memuat <c>README.md</c> folder
/// chat/topik aktif, merendernya ke HTML via <see cref="MarkdownHtmlRenderer"/> (Markdig),
/// lalu mengeksposnya sebagai <see cref="HtmlWebViewSource"/> untuk ditampilkan pada
/// <c>WebView</c> (Requirement 10.1).
///
/// Tautan media relatif di README di-resolve memakai tag <c>&lt;base&gt;</c> (URI folder)
/// dan dibuka di luar aplikasi oleh code-behind lewat <c>Launcher</c> (Requirement 10.2).
///
/// Bila <c>README.md</c> belum ada / kosong, ditampilkan pesan kosong yang jelas beserta
/// aksi "Rebuild" untuk membangkitkannya dari DB (Requirement 10.3).
/// </summary>
public partial class MarkdownViewerViewModel : ObservableObject
{
    private const string ReadmeFileName = "README.md";

    private readonly DocumentationService _documentation;

    private string _relativeFolder = string.Empty;
    private string _root = string.Empty;

    // Disetel oleh LoadAsync agar dipakai ulang oleh preview/save editor (Fase 3).
    private string _readmePath = string.Empty;
    private string _baseHref = string.Empty;

    // Isi README di disk saat sesi edit dimulai — baseline pembanding blok penanda (Req 11.4).
    private string _editBaseline = string.Empty;

    // Debounce render pratinjau saat mengetik (Req 11.1).
    private CancellationTokenSource? _previewCts;

    [ObservableProperty] private string _title = "Documentation";

    /// <summary>Sumber HTML untuk WebView; instance baru tiap render agar WebView memuat ulang.</summary>
    [ObservableProperty] private HtmlWebViewSource? _htmlSource;

    /// <summary>True bila ada konten README untuk ditampilkan (WebView terlihat).</summary>
    [ObservableProperty] private bool _hasContent;

    /// <summary>True bila README belum ada/kosong (panel pesan kosong terlihat).</summary>
    [ObservableProperty] private bool _isEmpty;

    /// <summary>Pesan yang dijelaskan saat README tidak tersedia (Requirement 10.3).</summary>
    [ObservableProperty] private string _emptyMessage = string.Empty;

    /// <summary>True selama rebuild/regenerasi berjalan.</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>True bila rebuild dapat dilakukan (root & folder diketahui).</summary>
    [ObservableProperty] private bool _canRebuild;

    // --- Editor (Fase 3, Requirement 11) ----------------------------------

    /// <summary>True saat mode edit aktif (split Editor + pratinjau).</summary>
    [ObservableProperty] private bool _isEditing;

    /// <summary>Teks Markdown mentah yang sedang disunting (terikat ke <c>Editor</c>).</summary>
    [ObservableProperty] private string _rawMarkdown = string.Empty;

    /// <summary>True bila tombol "Edit" boleh tampil (ada konten & tidak sedang edit).</summary>
    [ObservableProperty] private bool _showEditButton;

    /// <summary>True bila aksi editor (Save/Done) tampil (mode edit aktif).</summary>
    [ObservableProperty] private bool _showEditorActions;

    /// <summary>True bila WebView penampil (read-only) tampil.</summary>
    [ObservableProperty] private bool _showViewer;

    /// <summary>True bila ada peringatan untuk ditampilkan (mis. edit di dalam blok penanda).</summary>
    [ObservableProperty] private bool _hasWarning;

    /// <summary>Pesan peringatan/status editor.</summary>
    [ObservableProperty] private string _warningMessage = string.Empty;

    /// <summary>Diminta saat modal perlu ditutup.</summary>
    public event Action? CloseRequested;

    public MarkdownViewerViewModel(DocumentationService documentation)
    {
        _documentation = documentation ?? throw new ArgumentNullException(nameof(documentation));
    }

    /// <summary>
    /// Siapkan penampil untuk sebuah folder. <paramref name="relativeFolder"/> relatif terhadap
    /// <paramref name="root"/> (root download absolut aktif). Panggil sebelum <see cref="LoadAsync"/>.
    /// </summary>
    public void Initialize(string relativeFolder, string root)
    {
        _relativeFolder = relativeFolder ?? string.Empty;
        _root = root ?? string.Empty;
        Title = string.IsNullOrWhiteSpace(_relativeFolder) ? "Documentation" : _relativeFolder;
        CanRebuild = !string.IsNullOrWhiteSpace(_root);
    }

    /// <summary>
    /// Baca <c>README.md</c> dari <c>{root}/{folder}/</c>, render ke HTML, dan tampilkan di
    /// WebView. Bila file tidak ada / kosong → tampilkan pesan kosong (Requirement 10.3).
    /// </summary>
    public async Task LoadAsync()
    {
        // Keluar dari mode edit saat (re)load — tampilkan render read-only.
        IsEditing = false;
        HasWarning = false;
        WarningMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(_root))
        {
            ShowEmpty("Folder download belum dikonfigurasi. Atur folder lewat Configuration untuk membuka dokumentasi.");
            CanRebuild = false;
            return;
        }

        try
        {
            var folderAbs = ResolveFolder(_root, _relativeFolder);
            _readmePath = Path.Combine(folderAbs, ReadmeFileName);
            // base href = URI folder (diakhiri pemisah) agar tautan media relatif ter-resolve.
            _baseHref = new Uri(folderAbs + Path.DirectorySeparatorChar).AbsoluteUri;

            if (!File.Exists(_readmePath))
            {
                ShowEmpty($"Belum ada README.md untuk \"{_relativeFolder}\". Tekan Rebuild untuk membangkitkannya dari database.");
                return;
            }

            var markdown = await File.ReadAllTextAsync(_readmePath);
            if (string.IsNullOrWhiteSpace(markdown))
            {
                ShowEmpty($"README.md untuk \"{_relativeFolder}\" masih kosong. Tekan Rebuild untuk membangkitkannya dari database.");
                return;
            }

            _editBaseline = markdown;
            RenderHtml(markdown);
        }
        catch (Exception ex)
        {
            ShowEmpty($"Gagal memuat dokumentasi: {ex.Message}");
        }
    }

    /// <summary>Render <paramref name="markdown"/> ke HTML &amp; tampilkan pada WebView.</summary>
    private void RenderHtml(string markdown)
    {
        var html = MarkdownHtmlRenderer.Render(markdown, _baseHref);
        HtmlSource = new HtmlWebViewSource { Html = html };
        HasContent = true;
        IsEmpty = false;
        UpdateChrome();
    }

    /// <summary>
    /// Bangkitkan ulang <c>README.md</c> dari DB lalu muat ulang penampil (Requirement 10.3).
    /// </summary>
    [RelayCommand]
    private async Task RebuildAsync()
    {
        if (string.IsNullOrWhiteSpace(_root))
            return;

        IsBusy = true;
        try
        {
            await _documentation.RebuildFolderAsync(_relativeFolder);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            ShowEmpty($"Rebuild gagal: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Tutup modal.</summary>
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    // --- Editor commands (Fase 3, Requirement 11) -------------------------

    /// <summary>
    /// Masuk mode edit: tampilkan Markdown mentah + pratinjau langsung berdampingan
    /// (Requirement 11.1). Baseline blok penanda dicatat untuk deteksi suntingan di
    /// dalam penanda saat menyimpan (Requirement 11.4).
    /// </summary>
    [RelayCommand]
    private void Edit()
    {
        if (!HasContent)
            return;

        HasWarning = false;
        WarningMessage = string.Empty;
        RawMarkdown = _editBaseline;
        IsEditing = true;
        RenderHtml(_editBaseline); // pratinjau awal = konten saat ini
        UpdateChrome();
    }

    /// <summary>
    /// Simpan suntingan kembali ke <c>README.md</c> (Requirement 11.2). Teks di luar blok
    /// penanda dipertahankan apa adanya — sehingga regenerasi berikutnya tetap menjaganya
    /// (Requirement 11.3). Bila pengguna mengubah konten DI DALAM blok penanda, tampilkan
    /// peringatan bahwa perubahan itu akan tertimpa saat regenerasi (Requirement 11.4).
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!IsEditing || string.IsNullOrEmpty(_readmePath))
            return;

        try
        {
            var insideModified = DocumentationRenderer.InsideMarkersModified(_editBaseline, RawMarkdown);

            await File.WriteAllTextAsync(_readmePath, RawMarkdown);

            if (insideModified)
            {
                WarningMessage =
                    "Tersimpan. Catatan: Anda menyunting konten di dalam blok TELEGRAB:BEGIN/END — " +
                    "bagian itu akan tertimpa saat dokumentasi di-regenerate. Tulis catatan Anda di luar penanda agar tetap dipertahankan.";
                HasWarning = true;
            }
            else
            {
                WarningMessage = "Perubahan tersimpan.";
                HasWarning = true;
            }
        }
        catch (Exception ex)
        {
            WarningMessage = $"Gagal menyimpan: {ex.Message}";
            HasWarning = true;
        }
    }

    /// <summary>Keluar dari mode edit dan kembali ke tampilan render (memuat ulang dari disk).</summary>
    [RelayCommand]
    private async Task DoneAsync()
    {
        _previewCts?.Cancel();
        await LoadAsync();
    }

    /// <summary>
    /// Re-render pratinjau saat teks berubah (Requirement 11.1), di-debounce agar responsif
    /// tanpa memuat ulang WebView pada tiap ketukan.
    /// </summary>
    partial void OnRawMarkdownChanged(string value)
    {
        if (!IsEditing)
            return;
        SchedulePreview();
    }

    partial void OnHasContentChanged(bool value) => UpdateChrome();

    partial void OnIsEditingChanged(bool value) => UpdateChrome();

    private void SchedulePreview()
    {
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        _ = DebouncePreviewAsync(cts.Token);
    }

    private async Task DebouncePreviewAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(250, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested || !IsEditing)
            return;

        RenderHtml(RawMarkdown);
    }

    private void UpdateChrome()
    {
        ShowEditorActions = IsEditing;
        ShowEditButton = HasContent && !IsEditing;
        ShowViewer = HasContent && !IsEditing;
    }

    private void ShowEmpty(string message)
    {
        EmptyMessage = message;
        IsEmpty = true;
        HasContent = false;
        HtmlSource = null;
        UpdateChrome();
    }

    private static string ResolveFolder(string root, string relativeFolder)
    {
        var normalized = (relativeFolder ?? string.Empty).Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || normalized == ".")
            return Path.GetFullPath(root);

        var os = normalized.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(root, os));
    }
}
