using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Telegrab.Models;
using Telegrab.Services;

namespace Telegrab.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly ConfigService _configService;
    private readonly TelegramService _telegram;

    /// <summary>Dipicu saat login berhasil.</summary>
    public event Action? LoggedIn;

    [ObservableProperty] private int _apiId;
    [ObservableProperty] private string _apiHash = string.Empty;
    [ObservableProperty] private string _phoneNumber = string.Empty;
    [ObservableProperty] private string _name = string.Empty;

    /// <summary>True jika API Hash ditampilkan polos (toggle mata).</summary>
    [ObservableProperty] private bool _showApiHash;

    [ObservableProperty] private string _verificationCode = string.Empty;
    [ObservableProperty] private string _password = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedInput))]
    private bool _needCode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedInput))]
    private bool _needPassword;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = string.Empty;

    /// <summary>True jika sedang menunggu input lanjutan (kode atau password).</summary>
    public bool NeedInput => NeedCode || NeedPassword;

    public LoginViewModel(ConfigService configService, TelegramService telegram)
    {
        _configService = configService;
        _telegram = telegram;

        var cfg = _configService.Load();
        _apiId = cfg.ApiId;
        _apiHash = cfg.ApiHash;
        _phoneNumber = cfg.PhoneNumber;
        _name = cfg.Name;
    }

    /// <summary>Coba auto-login dengan session tersimpan. True jika berhasil masuk.</summary>
    public async Task<bool> TryAutoLoginAsync()
    {
        try
        {
            return await _telegram.TryAutoLoginAsync(_configService.Load());
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand]
    private void ToggleShowHash() => ShowApiHash = !ShowApiHash;

    [RelayCommand]
    private async Task OpenApiHelpAsync()
    {
        try
        {
            await Launcher.Default.OpenAsync("https://my.telegram.org/apps");
        }
        catch (Exception ex)
        {
            Status = $"Couldn't open link: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (ApiId == 0 || string.IsNullOrWhiteSpace(ApiHash) || string.IsNullOrWhiteSpace(PhoneNumber))
        {
            Status = "api_id, api_hash, and phone number are required.";
            return;
        }

        // Pertahankan konfigurasi yang sudah ada (mis. DownloadRoot) dan hanya
        // perbarui field kredensial — agar root download yang sudah dipilih TIDAK hilang saat
        // login ulang (bug: konfigurasi path hilang setelah restart).
        var account = _configService.Load();
        account.ApiId = ApiId;
        account.ApiHash = ApiHash.Trim();
        account.PhoneNumber = PhoneNumber.Trim();
        account.Name = Name.Trim();

        _configService.Save(account);
        _telegram.Init(account);

        IsBusy = true;
        Status = "Connecting...";
        try
        {
            var step = await _telegram.LoginAsync(account.PhoneNumber);
            await ProcessStepAsync(step);
        }
        catch (Exception ex)
        {
            Status = $"Failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        IsBusy = true;
        try
        {
            string? step;
            if (NeedCode)
            {
                NeedCode = false;
                step = await _telegram.LoginAsync(VerificationCode.Trim());
            }
            else if (NeedPassword)
            {
                NeedPassword = false;
                step = await _telegram.LoginAsync(Password);
                Password = string.Empty;
            }
            else
            {
                return;
            }

            await ProcessStepAsync(step);
        }
        catch (Exception ex)
        {
            Status = $"Failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ProcessStepAsync(string? step)
    {
        switch (step)
        {
            case null:
                Status = $"Signed in as {_telegram.Me?.first_name}.";
                LoggedIn?.Invoke();
                break;

            case "verification_code":
                NeedCode = true;
                Status = "A code was sent to your TELEGRAM APP (a message from 'Telegram'), " +
                         "not via SMS. Check Telegram on your phone/another device, then enter the code.";
                break;

            case "password":
                NeedPassword = true;
                Status = "This account uses 2FA. Enter your password.";
                break;

            case "name":
                var next = await _telegram.LoginAsync(string.IsNullOrWhiteSpace(Name) ? "User" : Name);
                await ProcessStepAsync(next);
                break;

            default:
                Status = $"Unknown login step: {step}";
                break;
        }
    }
}
