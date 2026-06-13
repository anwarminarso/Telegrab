using Telegrab.ViewModels;

namespace Telegrab.Views;

public partial class LoginPage : ContentPage
{
    private readonly LoginViewModel _vm;
    private readonly IServiceProvider _services;
    private bool _autoLoginTried;

    public LoginPage(LoginViewModel vm, IServiceProvider services)
    {
        InitializeComponent();
        _vm = vm;
        _services = services;
        BindingContext = vm;

        vm.LoggedIn += OnLoggedIn;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_autoLoginTried) return;
        _autoLoginTried = true;

        _vm.Status = "Checking saved session...";
        if (await _vm.TryAutoLoginAsync())
            OnLoggedIn();
        else
            _vm.Status = string.Empty;
    }

    private void OnLoggedIn()
    {
        // Ganti root window ke MainPage.
        var main = _services.GetRequiredService<MainPage>();
        if (Application.Current?.Windows.Count > 0)
        {
            var window = Application.Current.Windows[0];
            window.Page = new NavigationPage(main);
            App.ResizeToMain(window);
        }
    }
}
