using MauiIcons.Core;
using Telegrab.ViewModels;

namespace Telegrab.Views;

/// <summary>
/// Modal konfigurasi root download (Requirement 2). Menutup dirinya saat ViewModel meminta
/// (root berhasil disimpan atau dibatalkan).
/// </summary>
public partial class ConfigPage : ContentPage
{
    private readonly ConfigViewModel _vm;

    public ConfigPage(ConfigViewModel vm)
    {
        InitializeComponent();
        _ = new MauiIcon(); // workaround namespace url-style MauiIcons (lihat MainPage)
        _vm = vm;
        BindingContext = vm;

        vm.CloseRequested += OnCloseRequested;
    }

    private async void OnCloseRequested()
    {
        _vm.CloseRequested -= OnCloseRequested;
        await Navigation.PopModalAsync();
    }
}
