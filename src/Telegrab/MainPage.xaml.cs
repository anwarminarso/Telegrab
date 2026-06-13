using MauiIcons.Core;
using Telegrab.ViewModels;
using Telegrab.Views;

namespace Telegrab;

public partial class MainPage : ContentPage
{
	private readonly MainViewModel _vm;
	private readonly IServiceProvider _services;
	private bool _loaded;

	public MainPage(MainViewModel vm, IServiceProvider services)
	{
		InitializeComponent();
		_ = new MauiIcon(); // workaround namespace url-style MauiIcons
		_vm = vm;
		_services = services;
		BindingContext = vm;

		vm.OpenMediaRequested += OnOpenMedia;
		vm.OpenConfigRequested += OnOpenConfig;
		vm.OpenDocumentationRequested += OnOpenDocumentation;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		if (_loaded) return;
		_loaded = true;
		await _vm.LoadChatsCommand.ExecuteAsync(null);

		// Setelah login & chat dimuat: bila folder download belum dikonfigurasi, paksa pengguna
		// mengaturnya lewat modal Configuration (mandatory).
		_vm.RequestConfigIfNeeded();
	}

	private async void OnOpenMedia(Models.MediaGalleryRequest request)
	{
		await Navigation.PushModalAsync(new MediaViewerPage(request));
	}

	private async void OnOpenConfig(bool mandatory)
	{
		var page = _services.GetRequiredService<ConfigPage>();
		if (page.BindingContext is ConfigViewModel vm)
			vm.Initialize(mandatory);
		await Navigation.PushModalAsync(page);
	}

	private async void OnOpenDocumentation(Models.DocumentationRequest request)
	{
		var page = _services.GetRequiredService<MarkdownViewerPage>();
		page.Initialize(request.RelativeFolder, request.Root);
		await Navigation.PushModalAsync(page);
	}
}
