using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Telegrab.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : MauiWinUIApplication
{
	/// <summary>
	/// Initializes the singleton application object.  This is the first line of authored code
	/// executed, and as such is the logical equivalent of main() or WinMain().
	/// </summary>
	public App()
	{
		this.InitializeComponent();

		// Catat exception UI yang tak tertangani (penyebab crash 0xc000027b di Microsoft.UI.Xaml.dll).
		// Tidak di-set Handled: aplikasi tetap ditutup seperti sebelumnya, tetapi jejaknya tersimpan.
		UnhandledException += (_, e) =>
			Telegrab.Services.CrashLog.Write("WinUI.UnhandledException", e.Exception, e.Message);
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}

