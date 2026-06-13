using MauiIcons.Core;
using Telegrab.ViewModels;
using Telegrab.Views;

namespace Telegrab;

public partial class MainPage : ContentPage
{
	private readonly MainViewModel _vm;
	private bool _loaded;

	public MainPage(MainViewModel vm)
	{
		InitializeComponent();
		_ = new MauiIcon(); // workaround namespace url-style MauiIcons
		_vm = vm;
		BindingContext = vm;

		vm.OpenMediaRequested += OnOpenMedia;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		if (_loaded) return;
		_loaded = true;
		await _vm.LoadChatsCommand.ExecuteAsync(null);
	}

	private async void OnOpenMedia(Models.MediaGalleryRequest request)
	{
		await Navigation.PushModalAsync(new MediaViewerPage(request));
	}
}
