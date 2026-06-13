using CommunityToolkit.Maui;
using MauiIcons.Core;
using MauiIcons.Material;
using Microsoft.Extensions.Logging;
using Telegrab.Services;
using Telegrab.ViewModels;
using Telegrab.Views;

namespace Telegrab;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseMauiCommunityToolkit()
			.UseMauiCommunityToolkitMediaElement(false)
			.UseMaterialMauiIcons()
			.UseMauiIconsCore(x => x.SetDefaultFontOverride(true))
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// Services
		builder.Services.AddSingleton<ConfigService>();
		builder.Services.AddSingleton<TelegramService>();
		builder.Services.AddSingleton<ManifestDbService>();
		builder.Services.AddSingleton<DocumentationService>();
		builder.Services.AddSingleton<DbLifecycleCoordinator>();
		builder.Services.AddSingleton<DownloadQueueService>();

		// ViewModels
		builder.Services.AddSingleton<LoginViewModel>();
		builder.Services.AddSingleton<MainViewModel>();
		builder.Services.AddTransient<ConfigViewModel>();
		builder.Services.AddTransient<MarkdownViewerViewModel>();

		// Pages
		builder.Services.AddSingleton<LoginPage>();
		builder.Services.AddSingleton<MainPage>();
		builder.Services.AddTransient<ConfigPage>();
		builder.Services.AddTransient<MarkdownViewerPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		var app = builder.Build();

		// Wire DB lifecycle ke root download: buka DB saat startup bila root valid, dan
		// pasang langganan RootChanged (tutup DB lama → buka DB root baru). Resolusi di sini
		// memastikan singleton terbentuk sehingga langganan event aktif sejak awal.
		var dbLifecycle = app.Services.GetRequiredService<DbLifecycleCoordinator>();
		dbLifecycle.Initialize();

		return app;
	}
}
