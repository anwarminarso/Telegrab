using Telegrab.Views;

namespace Telegrab;

public partial class App : Application
{
	/// <summary>Nama aplikasi — diubah di satu tempat ini saja.</summary>
	public const string AppName = "Telegrab";

	private readonly IServiceProvider _services;

	/// <summary>Ukuran jendela saat halaman login (pas dengan lebar kartu login).</summary>
	public const double LoginWindowWidth = 520;
	public const double LoginWindowHeight = 760;

	/// <summary>Ukuran jendela default halaman utama (dipakai bila belum ada setelan tersimpan).</summary>
	public const double MainWindowWidth = 1200;
	public const double MainWindowHeight = 800;

	private const double MainWindowMinWidth = 800;
	private const double MainWindowMinHeight = 600;

	// Kunci penyimpanan ukuran jendela halaman utama terakhir.
	private const string KeyMainWidth = "main_window_width";
	private const string KeyMainHeight = "main_window_height";

	public App(IServiceProvider services)
	{
		InitializeComponent();
		_services = services;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var login = _services.GetRequiredService<LoginPage>();
		var window = new Window(new NavigationPage(login))
		{
			Title = AppName,
			Width = LoginWindowWidth,
			Height = LoginWindowHeight,
			MinimumWidth = LoginWindowWidth,
			MinimumHeight = 600
		};

		window.TitleBar = new TitleBar
		{
			Title = AppName,
			Icon = ImageSource.FromFile("brandlogo.png"),
			ForegroundColor = Colors.White,
			BackgroundColor = Color.FromArgb("#17212B")
		};

		// Tempatkan jendela login di tengah layar (mirip CenterScreen di WinForms).
		window.Created += (_, _) => CenterOnScreen(window);

		return window;
	}

	/// <summary>Memposisikan jendela tepat di tengah area kerja layar (khusus Windows).</summary>
	private static void CenterOnScreen(Window window)
	{
#if WINDOWS
		if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
			return;

		var appWindow = native.AppWindow;
		if (appWindow is null) return;

		var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
			appWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
		if (area is null) return;

		var x = area.WorkArea.X + (area.WorkArea.Width - appWindow.Size.Width) / 2;
		var y = area.WorkArea.Y + (area.WorkArea.Height - appWindow.Size.Height) / 2;
		appWindow.Move(new Windows.Graphics.PointInt32(x, y));
#endif
	}

	/// <summary>
	/// Pindah ke ukuran jendela halaman utama: memakai setelan terakhir yang tersimpan
	/// (atau default bila belum ada), lalu menyimpan setiap perubahan ukuran.
	/// Status maximize/fullscreen sengaja tidak disimpan.
	/// </summary>
	public static void ResizeToMain(Window window)
	{
		window.MinimumWidth = MainWindowMinWidth;
		window.MinimumHeight = MainWindowMinHeight;

		var width = Preferences.Get(KeyMainWidth, MainWindowWidth);
		var height = Preferences.Get(KeyMainHeight, MainWindowHeight);

		// Jaga-jaga bila nilai tersimpan rusak/terlalu kecil.
		window.Width = Math.Max(width, MainWindowMinWidth);
		window.Height = Math.Max(height, MainWindowMinHeight);

		window.SizeChanged -= OnMainWindowSizeChanged;
		window.SizeChanged += OnMainWindowSizeChanged;
	}

	private static void OnMainWindowSizeChanged(object? sender, EventArgs e)
	{
		if (sender is not Window window) return;

		// Jangan simpan saat maximize/fullscreen — hanya simpan ukuran "restored".
		if (IsMaximized(window)) return;

		var width = window.Width;
		var height = window.Height;
		if (width < MainWindowMinWidth || height < MainWindowMinHeight) return;

		Preferences.Set(KeyMainWidth, width);
		Preferences.Set(KeyMainHeight, height);
	}

	/// <summary>Apakah jendela sedang dalam keadaan maximize (khusus Windows).</summary>
	private static bool IsMaximized(Window window)
	{
#if WINDOWS
		if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window native &&
			native.AppWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
		{
			return presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;
		}
#endif
		return false;
	}
}
