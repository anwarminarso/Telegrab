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
		builder.Services.AddSingleton<DownloadManifestService>();
		builder.Services.AddSingleton<DownloadQueueService>();

		// ViewModels
		builder.Services.AddSingleton<LoginViewModel>();
		builder.Services.AddSingleton<MainViewModel>();

		// Pages
		builder.Services.AddSingleton<LoginPage>();
		builder.Services.AddSingleton<MainPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
