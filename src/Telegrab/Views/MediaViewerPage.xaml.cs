using System.Diagnostics;
using System.IO;
using CommunityToolkit.Maui.Views;
using Telegrab.Models;

namespace Telegrab.Views;

public partial class MediaViewerPage : ContentPage
{
    private readonly IReadOnlyList<MediaPart> _items;
    private readonly Func<MediaPart, Task<string?>> _ensure;
    private int _index;
    private bool _shown;

    // Auto-hide chrome
    private IDispatcherTimer? _idleTimer;
    private bool _chromeVisible = true;
    private static readonly TimeSpan ChromeIdle = TimeSpan.FromSeconds(2.5);

    // Sinkronisasi filmstrip <-> indeks aktif (cegah re-entrancy SelectionChanged)
    private bool _syncingSelection;

    // True saat foto sedang di-zoom (pan aktif): klik-zona/swipe dimatikan.
    private bool _isZoomed;

#if WINDOWS
    private ZoomPanController? _imageZoom;
    private ZoomPanController? _videoZoom;
    private Microsoft.UI.Xaml.UIElement? _accelHost;
    private readonly List<Microsoft.UI.Xaml.Input.KeyboardAccelerator> _accels = new();
#endif

    public MediaViewerPage(MediaGalleryRequest request)
    {
        InitializeComponent();
        _items = request.Items;
        _index = request.StartIndex;
        _ensure = request.EnsureDownloaded;

        // Filmstrip hanya berguna bila ada lebih dari satu media.
        if (_items.Count > 1)
        {
            Filmstrip.ItemsSource = _items;
            FilmstripBar.IsVisible = true;
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_shown) return;
        _shown = true;

        StartChromeWatcher();
        HookKeyboard();
        await ShowCurrentAsync();
    }

    private async Task ShowCurrentAsync()
    {
        if (_index < 0 || _index >= _items.Count) return;
        var part = _items[_index];

        UpdateChrome();

        // Tipe file lain (bukan foto/video): buka dengan aplikasi default lalu tutup.
        if (part.Kind is not (MediaKind.Photo or MediaKind.Video))
        {
            var p = await _ensure(part);
            if (!string.IsNullOrEmpty(p))
            {
                try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); }
                catch { /* abaikan */ }
            }
            await Navigation.PopModalAsync();
            return;
        }

        Loading.IsVisible = Loading.IsRunning = true;
        var path = await _ensure(part);
        Loading.IsVisible = Loading.IsRunning = false;

        if (string.IsNullOrEmpty(path))
        {
            Title = "Failed to load media";
            return;
        }

        Title = Path.GetFileName(path);

        _isZoomed = false;

        if (part.Kind == MediaKind.Photo)
        {
            VideoView.Stop();
            VideoContainer.IsVisible = false;

            ImageView.Source = ImageSource.FromFile(path);
            ImageContainer.IsVisible = true;
#if WINDOWS
            EnsureImageZoom();
            _imageZoom?.Reset();
#endif
        }
        else // Video
        {
            ImageContainer.IsVisible = false;

            VideoView.Source = MediaSource.FromFile(path);
            VideoContainer.IsVisible = true;
#if WINDOWS
            EnsureVideoZoom();
            _videoZoom?.Reset();
#endif
        }

        UpdateTapLayerState();
        SyncFilmstrip();
        ShowChrome();
    }

    private void UpdateChrome()
    {
        var hasMany = _items.Count > 1;
        CounterLabel.Text = hasMany ? $"{_index + 1} / {_items.Count}" : string.Empty;
        PrevButton.IsVisible = hasMany && _index > 0;
        NextButton.IsVisible = hasMany && _index < _items.Count - 1;
    }

    // ---- Auto-hide chrome -------------------------------------------------

    private void StartChromeWatcher()
    {
        _idleTimer = Dispatcher.CreateTimer();
        _idleTimer.Interval = ChromeIdle;
        _idleTimer.Tick += (_, _) => HideChrome();
        _idleTimer.Start();
    }

    private void ShowChrome()
    {
        _idleTimer?.Stop();
        if (!_chromeVisible)
        {
            _chromeVisible = true;
            Chrome.InputTransparent = false;
            _ = Chrome.FadeToAsync(1, 150);
        }
        _idleTimer?.Start();
    }

    private void HideChrome()
    {
        _idleTimer?.Stop();
        if (!_chromeVisible) return;
        _chromeVisible = false;
        Chrome.InputTransparent = true; // biar klik-zona di bawahnya tetap aktif
        _ = Chrome.FadeToAsync(0, 250);
    }

    private void ToggleChrome()
    {
        if (_chromeVisible) HideChrome();
        else ShowChrome();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e) => ShowChrome();

    // ---- Klik-zona & swipe ------------------------------------------------

    /// <summary>
    /// Klik-zona hanya aktif untuk foto yang tidak sedang di-zoom. Saat zoom aktif
    /// pointer diteruskan ke pan; saat video diputar diteruskan ke kontrol playback.
    /// </summary>
    private void UpdateTapLayerState()
    {
        var enabled = ImageContainer.IsVisible && !_isZoomed;
        TapLayer.InputTransparent = !enabled;
    }

    private void OnTapLeft(object? sender, TappedEventArgs e) => OnPrev(this, EventArgs.Empty);
    private void OnTapRight(object? sender, TappedEventArgs e) => OnNext(this, EventArgs.Empty);
    private void OnTapCenter(object? sender, TappedEventArgs e) => ToggleChrome();

    private void OnSwipeLeft(object? sender, SwipedEventArgs e) => OnNext(this, EventArgs.Empty);
    private void OnSwipeRight(object? sender, SwipedEventArgs e) => OnPrev(this, EventArgs.Empty);

    // ---- Filmstrip --------------------------------------------------------

    private void SyncFilmstrip()
    {
        if (_items.Count <= 1) return;

        _syncingSelection = true;
        Filmstrip.SelectedItem = _items[_index];
        _syncingSelection = false;

        // Animasi scroll mahal untuk daftar sangat panjang: matikan bila ribuan item.
        var animate = _items.Count <= 200;
        try { Filmstrip.ScrollTo(_index, position: ScrollToPosition.Center, animate: animate); }
        catch { /* daftar mungkin belum ter-render */ }
    }

    private void OnFilmstripSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        if (Filmstrip.SelectedItem is MediaPart part)
        {
            var idx = IndexOf(part);
            if (idx >= 0) GoTo(idx);
        }
    }

    private int IndexOf(MediaPart part)
    {
        for (var i = 0; i < _items.Count; i++)
            if (ReferenceEquals(_items[i], part)) return i;
        return -1;
    }

    private async void OnPrev(object? sender, EventArgs e)
    {
        if (_index <= 0) return;
        _index--;
        await ShowCurrentAsync();
    }

    private async void OnNext(object? sender, EventArgs e)
    {
        if (_index >= _items.Count - 1) return;
        _index++;
        await ShowCurrentAsync();
    }

    private async void GoTo(int index)
    {
        index = Math.Clamp(index, 0, _items.Count - 1);
        if (index == _index) return;
        _index = index;
        await ShowCurrentAsync();
    }

    private async void OnClose(object? sender, EventArgs e)
    {
        Cleanup();
        await Navigation.PopModalAsync();
    }

    /// <summary>
    /// Pembersihan idempotent saat viewer ditutup: hentikan timer, lepas pemutar video,
    /// dan copot zoom/keyboard. Dipanggil dari tombol close/Escape maupun OnDisappearing.
    /// </summary>
    private bool _cleaned;
    private void Cleanup()
    {
        if (_cleaned) return;
        _cleaned = true;

        _idleTimer?.Stop();
        VideoView.Stop();
        // Lepas handler MediaElement agar pemutar native (MediaPlayerElement/MediaPlayer)
        // benar-benar dibebaskan saat viewer ditutup; tanpa ini bisa terjadi kebocoran
        // resource & audio yang masih jalan di latar. Hanya dijalankan sekali saat KELUAR,
        // jadi tidak memengaruhi navigasi bolak-balik foto<->video di dalam viewer.
        VideoView.Handler?.DisconnectHandler();
#if WINDOWS
        _imageZoom?.Detach();
        _videoZoom?.Detach();
        _imageZoom = null;
        _videoZoom = null;
        UnhookKeyboard();
#endif
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        Cleanup(); // jaring pengaman bila ditutup bukan lewat tombol close
    }

#if WINDOWS
    private void EnsureImageZoom()
    {
        if (_imageZoom != null) return;
        if (Root.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement host &&
            ImageContainer.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement cont &&
            ImageView.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement ch)
            _imageZoom = new ZoomPanController(host, cont, ch, OnZoomChanged);
    }

    private void EnsureVideoZoom()
    {
        if (_videoZoom != null) return;
        if (Root.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement host &&
            VideoContainer.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement cont &&
            VideoView.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement ch)
            _videoZoom = new ZoomPanController(host, cont, ch, OnZoomChanged);
    }

    /// <summary>Dipanggil controller native saat status zoom berubah (UI thread).</summary>
    private void OnZoomChanged(bool zoomed)
    {
        _isZoomed = zoomed;
        UpdateTapLayerState();
    }

    private void HookKeyboard()
    {
        void Attach()
        {
            if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window w &&
                w.Content is Microsoft.UI.Xaml.UIElement content)
            {
                _accelHost = content;
                AddAccel(Windows.System.VirtualKey.Escape, () => OnClose(this, EventArgs.Empty));
                AddAccel(Windows.System.VirtualKey.Left, () => OnPrev(this, EventArgs.Empty));
                AddAccel(Windows.System.VirtualKey.Right, () => OnNext(this, EventArgs.Empty));
                // Space sengaja TIDAK di-bind ke "next" agar tidak bentrok dengan
                // play/pause pada kontrol pemutar video.
                AddAccel(Windows.System.VirtualKey.Home, () => GoTo(0));
                AddAccel(Windows.System.VirtualKey.End, () => GoTo(_items.Count - 1));
            }
        }

        if (Window?.Handler?.PlatformView != null) Attach();
        else Loaded += (_, _) => Attach();
    }

    private void AddAccel(Windows.System.VirtualKey key, Action action)
    {
        if (_accelHost == null) return;
        var acc = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = key,
            Modifiers = Windows.System.VirtualKeyModifiers.None
        };
        acc.Invoked += (_, args) => { args.Handled = true; action(); };
        _accelHost.KeyboardAccelerators.Add(acc);
        _accels.Add(acc);
    }

    private void UnhookKeyboard()
    {
        if (_accelHost == null) return;
        foreach (var a in _accels)
            _accelHost.KeyboardAccelerators.Remove(a);
        _accels.Clear();
        _accelHost = null;
    }
#endif
}

#if WINDOWS
/// <summary>
/// Mengelola zoom (scroll wheel, mengikuti kursor) dan pan (klik-tahan-geser) pada sebuah
/// elemen native WinUI menggunakan <see cref="Microsoft.UI.Xaml.Media.CompositeTransform"/>.
/// </summary>
internal sealed class ZoomPanController
{
    private const double MinScale = 1.0;
    private const double MaxScale = 8.0;

    private readonly Microsoft.UI.Xaml.FrameworkElement _eventHost;
    private readonly Microsoft.UI.Xaml.FrameworkElement _container;
    private readonly Microsoft.UI.Xaml.FrameworkElement _child;
    private readonly Microsoft.UI.Xaml.Media.CompositeTransform _transform = new();
    private readonly Action<bool>? _onZoomChanged;

    private double _scale = 1.0;
    private double _tx;
    private double _ty;
    private bool _zoomedState;

    private bool _panning;
    private Windows.Foundation.Point _panStart;
    private double _panStartTx;
    private double _panStartTy;

    /// <param name="eventHost">
    /// Elemen leluhur (mis. Root) tempat event pointer didengarkan. Ini penting karena
    /// lapisan klik-zona berada di atas media: event pointer/wheel naik (bubble) ke leluhur,
    /// bukan turun ke sibling, sehingga zoom harus mendengarkan di leluhur bersama.
    /// </param>
    /// <param name="container">Wadah media (untuk ukuran clamp & cek visibilitas).</param>
    /// <param name="child">Elemen yang ditransform (Image/Video).</param>
    public ZoomPanController(
        Microsoft.UI.Xaml.FrameworkElement eventHost,
        Microsoft.UI.Xaml.FrameworkElement container,
        Microsoft.UI.Xaml.FrameworkElement child,
        Action<bool>? onZoomChanged = null)
    {
        _eventHost = eventHost;
        _container = container;
        _child = child;
        _onZoomChanged = onZoomChanged;

        _child.RenderTransformOrigin = new Windows.Foundation.Point(0, 0);
        _child.RenderTransform = _transform;

        _eventHost.PointerWheelChanged += OnWheel;
        _eventHost.PointerPressed += OnPressed;
        _eventHost.PointerMoved += OnMoved;
        _eventHost.PointerReleased += OnReleased;
        _eventHost.PointerCanceled += OnReleased;
        _eventHost.DoubleTapped += OnDoubleTapped;
    }

    public void Detach()
    {
        _eventHost.PointerWheelChanged -= OnWheel;
        _eventHost.PointerPressed -= OnPressed;
        _eventHost.PointerMoved -= OnMoved;
        _eventHost.PointerReleased -= OnReleased;
        _eventHost.PointerCanceled -= OnReleased;
        _eventHost.DoubleTapped -= OnDoubleTapped;
    }

    /// <summary>True bila wadah media yang dikelola sedang ditampilkan.</summary>
    private bool IsActiveTarget => _container.Visibility == Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>Kembalikan ke skala 1 dan posisi awal.</summary>
    public void Reset()
    {
        _scale = 1.0;
        _tx = 0;
        _ty = 0;
        _panning = false;
        Apply();
    }

    private void OnWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!IsActiveTarget) return;
        var delta = e.GetCurrentPoint(_container).Properties.MouseWheelDelta; // kelipatan 120
        if (delta == 0) return;

        var newScale = Math.Clamp(_scale * Math.Pow(1.1, delta / 120.0), MinScale, MaxScale);
        if (Math.Abs(newScale - _scale) < 0.0001) return;

        // Titik konten tepat di bawah kursor, dalam ruang lokal anak.
        // GetCurrentPoint(_child) sudah membalik transform saat ini, jadi nilai p ini
        // valid apa pun keadaan zoom/letterbox/DPI.
        var p = e.GetCurrentPoint(_child).Position;

        // Pertahankan titik p di posisi layar yang sama setelah skala berubah:
        // layar = S*p + T  =>  agar tetap: T' = T - (S' - S) * p
        _tx -= (newScale - _scale) * p.X;
        _ty -= (newScale - _scale) * p.Y;
        _scale = newScale;

        Apply();
        e.Handled = true;
    }

    private void OnPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!IsActiveTarget) return;
        if (_scale <= MinScale + 0.0001) return; // tidak perlu pan saat belum zoom

        _panning = true;
        _panStart = e.GetCurrentPoint(_container).Position;
        _panStartTx = _tx;
        _panStartTy = _ty;
        _eventHost.CapturePointer(e.Pointer);
    }

    private void OnMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_panning) return;

        var pos = e.GetCurrentPoint(_container).Position;
        _tx = _panStartTx + (pos.X - _panStart.X);
        _ty = _panStartTy + (pos.Y - _panStart.Y);
        Apply();
    }

    private void OnReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        _eventHost.ReleasePointerCapture(e.Pointer);
    }

    private void OnDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (!IsActiveTarget) return;
        Reset();
    }

    private void Apply()
    {
        if (_scale <= MinScale + 0.0001)
        {
            _scale = MinScale;
            _tx = 0;
            _ty = 0;
        }
        else
        {
            var w = _container.ActualWidth;
            var h = _container.ActualHeight;
            if (w > 0 && h > 0)
            {
                _tx = Math.Clamp(_tx, (MinScale - _scale) * w, 0);
                _ty = Math.Clamp(_ty, (MinScale - _scale) * h, 0);
            }
        }

        _transform.ScaleX = _scale;
        _transform.ScaleY = _scale;
        _transform.TranslateX = _tx;
        _transform.TranslateY = _ty;

        var zoomed = _scale > MinScale + 0.0001;
        if (zoomed != _zoomedState)
        {
            _zoomedState = zoomed;
            _onZoomChanged?.Invoke(zoomed);
        }
    }
}
#endif
