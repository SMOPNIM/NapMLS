using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NapMLS.UI.Models;
using NapMLS.UI.Services;

namespace NapMLS.UI.ViewModels;

/// <summary>
/// P1d-2b: Setup page — two-step flow with real FFI.
/// Step 1: NapCat connection config + test.
/// Step 2: Identity generation with real MlsClient.
/// </summary>
public partial class SetupViewModel : ViewModelBase
{
    private readonly Action<AppConfig> _onCompleted;
    private readonly AppConfig _config;
    private readonly string _configPath;
    private IntPtr _provider;
    private bool _ffiInitialized;

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

    private string DbPath
    {
        get
        {
            var dir = Path.GetDirectoryName(_configPath) ?? "";
            return Path.Combine(dir, "mls_data.db");
        }
    }

    public SetupViewModel(AppConfig config, string configPath, Action<AppConfig> onCompleted)
    {
        _config = config;
        _configPath = configPath;
        _onCompleted = onCompleted;

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

    private void EnsureFfiInitialized()
    {
        if (_ffiInitialized) return;

        // P1d-2b: use fixed test key. P1d-5: derive from user password via Argon2id.
        byte[] testKey = SHA256.HashData(Encoding.UTF8.GetBytes("napmls-test-password"));
        unsafe
        {
            fixed (byte* pKey = testKey)
            {
                NapMlsNative.napmls_init(pKey, testKey.Length);
            }
        }
        _ffiInitialized = true;
    }

    private IntPtr GetOrCreateProvider()
    {
        if (_provider != IntPtr.Zero) return _provider;

        EnsureFfiInitialized();

        var dir = Path.GetDirectoryName(DbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var dbPathBytes = Encoding.UTF8.GetBytes(DbPath);
        unsafe
        {
            fixed (byte* pDbPath = dbPathBytes)
            {
                _provider = NapMlsNative.napmls_provider_new_from_file(pDbPath, null);
            }
        }

        if (_provider == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create MLS provider");

        return _provider;
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

        _config.NapCatHost = NapCatHost;
        _config.NapCatPort = NapCatPort;
        _config.NapCatToken = NapCatToken;
        AppConfigService.Save(_config, _configPath);

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
            await Task.Run(() =>
            {
                var provider = GetOrCreateProvider();
                var nameBytes = Encoding.UTF8.GetBytes(Username);

                // Try to load existing identity first
                IntPtr identity;
                unsafe
                {
                    fixed (byte* pName = nameBytes)
                    {
                        var rc = NapMlsNative.napmls_load_identity(
                            provider, pName, nameBytes.Length, &identity, null);

                        if (rc == NapMlsNative.NAPMLS_OK && identity != IntPtr.Zero)
                        {
                            // Loaded existing identity
                            SetFingerprintFromIdentity(identity);
                            return;
                        }

                        // Not found — create new
                        rc = NapMlsNative.napmls_create_identity(
                            provider, pName, nameBytes.Length, &identity, null);
                        if (rc != NapMlsNative.NAPMLS_OK || identity == IntPtr.Zero)
                            throw new InvalidOperationException($"Failed to create identity: rc={rc}");

                        // Register for future loading
                        NapMlsNative.napmls_register_identity(
                            provider, pName, nameBytes.Length, identity, null);

                        SetFingerprintFromIdentity(identity);
                    }
                }
            });

            IsIdentityGenerated = true;

            _config.IdentityUsername = Username;
            _config.IdentityFingerprint = FingerprintHex;
            _config.IdentitySafetyCode = SafetyCode;
            AppConfigService.Save(_config, _configPath);
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

    private unsafe void SetFingerprintFromIdentity(IntPtr identity)
    {
        NapMlsNative.NapMlsBytes fp = default;
        var rc = NapMlsNative.napmls_identity_fingerprint(identity, &fp, null);
        if (rc != NapMlsNative.NAPMLS_OK || fp.len < 8)
            throw new InvalidOperationException($"Failed to get fingerprint: rc={rc}");

        var fpBytes = NapMlsNative.ReadBytes(fp);
        NapMlsNative.napmls_free_bytes(fp);

        FingerprintHex = Convert.ToHexString(fpBytes[..8]).ToUpperInvariant();
        SafetyCode = SafetyCodeFormatter.Format(fpBytes[..8]);
    }

    [RelayCommand]
    private void CopySafetyCode()
    {
        // Clipboard handled in View code-behind via TopLevel
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
        AppConfigService.Save(_config, _configPath);

        // Free provider — will be recreated by the next MlsClient
        if (_provider != IntPtr.Zero)
        {
            NapMlsNative.napmls_provider_free(_provider);
            _provider = IntPtr.Zero;
        }

        _onCompleted(_config);
    }
}
