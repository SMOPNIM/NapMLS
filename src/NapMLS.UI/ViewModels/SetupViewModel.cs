using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NapMLS.UI.Models;
using NapMLS.UI.Services;

namespace NapMLS.UI.ViewModels;

/// <summary>
/// P1d-2: Setup page — two-step flow.
/// Step 1: NapCat connection config + test.
/// Step 2: Identity generation + safety code display.
/// Uses fake identity data for now; real FFI in P1d-2b.
/// </summary>
public partial class SetupViewModel : ViewModelBase
{
    private readonly Action<AppConfig> _onCompleted;
    private readonly AppConfig _config;

    // -- Step tracking --
    [ObservableProperty]
    public partial int CurrentStep { get; set; } = 1;

    [ObservableProperty]
    public partial bool IsStep1Visible { get; set; } = true;

    [ObservableProperty]
    public partial bool IsStep2Visible { get; set; }

    // -- Step 1: NapCat config --
    [ObservableProperty]
    public partial string NapCatHost { get; set; }

    [ObservableProperty]
    public partial int NapCatPort { get; set; }

    [ObservableProperty]
    public partial string? NapCatToken { get; set; }

    [ObservableProperty]
    public partial bool IsConnecting { get; set; }

    [ObservableProperty]
    public partial bool? IsConnected { get; set; }

    [ObservableProperty]
    public partial string ConnectionStatus { get; set; } = "未连接";

    // -- Step 2: Identity --
    [ObservableProperty]
    public partial string? Username { get; set; }

    [ObservableProperty]
    public partial bool IsIdentityGenerated { get; set; }

    [ObservableProperty]
    public partial string? SafetyCode { get; set; }

    [ObservableProperty]
    public partial string? FingerprintHex { get; set; }

    [ObservableProperty]
    public partial bool IsGenerating { get; set; }

    // -- Shared --
    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string StepTitle { get; set; } = "步骤 1/2：NapCat 连接配置";

    public SetupViewModel(AppConfig config, Action<AppConfig> onCompleted)
    {
        _config = config;
        _onCompleted = onCompleted;

        // Restore saved values
        NapCatHost = config.NapCatHost;
        NapCatPort = config.NapCatPort;
        NapCatToken = config.NapCatToken;

        // If identity already exists, skip to step 2 with it pre-loaded
        if (!string.IsNullOrEmpty(config.IdentitySafetyCode))
        {
            Username = config.IdentityUsername;
            SafetyCode = config.IdentitySafetyCode;
            FingerprintHex = config.IdentityFingerprint;
            IsIdentityGenerated = true;
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        ErrorMessage = null;
        IsConnecting = true;
        IsConnected = null;
        ConnectionStatus = "连接中...";

        try
        {
            // P1d-2a: simulate connection test
            await Task.Delay(800);

            // TODO: P1d-2b — real NapCat WebSocket test
            IsConnected = true;
            ConnectionStatus = $"已连接 ws://{NapCatHost}:{NapCatPort}";
        }
        catch (Exception ex)
        {
            IsConnected = false;
            ConnectionStatus = "连接失败";
            ErrorMessage = $"连接失败: {ex.Message}";
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanProceedStep1))]
    private void ProceedToStep2()
    {
        ErrorMessage = null;

        // Save NapCat config
        _config.NapCatHost = NapCatHost;
        _config.NapCatPort = NapCatPort;
        _config.NapCatToken = NapCatToken;
        AppConfigService.Save(_config);

        CurrentStep = 2;
        IsStep1Visible = false;
        IsStep2Visible = true;
        StepTitle = "步骤 2/2：身份生成";
    }

    private bool CanProceedStep1() => IsConnected == true;

    partial void OnIsConnectedChanged(bool? value)
    {
        ProceedToStep2Command.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task GenerateIdentityAsync()
    {
        ErrorMessage = null;

        if (string.IsNullOrWhiteSpace(Username))
        {
            ErrorMessage = "请输入用户名";
            return;
        }

        IsGenerating = true;

        try
        {
            // P1d-2a: fake fingerprint (8 random bytes)
            await Task.Delay(600);

            byte[] fakeFingerprint = new byte[8];
            Random.Shared.NextBytes(fakeFingerprint);

            FingerprintHex = Convert.ToHexString(fakeFingerprint).ToUpperInvariant();
            SafetyCode = SafetyCodeFormatter.Format(fakeFingerprint);
            IsIdentityGenerated = true;

            // Persist
            _config.IdentityUsername = Username;
            _config.IdentityFingerprint = FingerprintHex;
            _config.IdentitySafetyCode = SafetyCode;
            AppConfigService.Save(_config);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"身份生成失败: {ex.Message}";
        }
        finally
        {
            IsGenerating = false;
        }
    }

    [RelayCommand]
    private void CopySafetyCode()
    {
        if (SafetyCode != null)
        {
            // Avalonia clipboard — handled via TopLevel in View code-behind
            // This is a placeholder; actual clipboard set in SetupView.axaml.cs
        }
    }

    [RelayCommand]
    private void Complete()
    {
        if (!IsIdentityGenerated)
        {
            ErrorMessage = "请先生成身份";
            return;
        }

        _config.RiskWarningAccepted = true;
        AppConfigService.Save(_config);
        _onCompleted(_config);
    }
}
