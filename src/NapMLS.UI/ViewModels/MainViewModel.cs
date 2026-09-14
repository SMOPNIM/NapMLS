using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using NapMLS.Core;
using NapMLS.UI.Models;
using NapMLS.UI.Services;

namespace NapMLS.UI.ViewModels;

/// <summary>
/// P1d-2/5: Main window ViewModel with startup state machine.
///
/// State machine:
///   config.json missing → RiskWarning → Setup Step 1
///   config.json exists
///     → risk not accepted → RiskWarning
///     → risk accepted, no identity → Setup Step 2
///     → risk accepted + identity → GroupList
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    public NavigationService Navigation { get; } = new();

    [ObservableProperty]
    public partial string WindowTitle { get; set; } = "NapMLS - 加密子群";

    private AppConfig _config = new();
    private SqliteStorage? _storage;
    private MlsService? _mls;

    public MainViewModel()
    {
        _config = AppConfigService.Load();
        RunStartupStateMachine();
    }

    private string GetAppDir()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NapMLS");
    }

    private SqliteStorage GetStorage()
    {
        if (_storage == null)
        {
            var dbPath = Path.Combine(GetAppDir(), "napmls.db");
            _storage = new SqliteStorage(dbPath);
        }
        return _storage;
    }

    private MlsService? GetMls()
    {
        if (_mls != null) return _mls;

        if (string.IsNullOrEmpty(_config.IdentityUsername)) return null;

        var dbPath = Path.Combine(GetAppDir(), "napmls.db");

        // Derive encryption key from username (P1d-5: real Argon2id deferred)
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(_config.IdentityUsername));

        _mls = MlsService.Open(dbPath, key, _config.IdentityUsername);
        return _mls;
    }

    private void RunStartupStateMachine()
    {
        if (!_config.RiskWarningAccepted)
        {
            Navigation.NavigateTo(new RiskWarningViewModel(OnRiskAccepted));
        }
        else if (string.IsNullOrEmpty(_config.IdentitySafetyCode))
        {
            Navigation.NavigateTo(new SetupViewModel(_config, OnSetupCompleted));
        }
        else
        {
            NavigateToGroups();
        }
    }

    private void OnRiskAccepted()
    {
        _config.RiskWarningAccepted = true;
        AppConfigService.Save(_config);
        Navigation.Clear();
        Navigation.NavigateTo(new SetupViewModel(_config, OnSetupCompleted));
    }

    private void OnSetupCompleted(AppConfig config)
    {
        _config = config;
        Navigation.Clear();
        NavigateToGroups();
    }

    private void NavigateToGroups()
    {
        var storage = GetStorage();
        var fp = Convert.FromHexString(_config.IdentityFingerprint!);
        var mls = GetMls();
        var vm = new GroupListViewModel(_config, storage, fp, mls)
        {
            Navigation = Navigation,
        };
        Navigation.NavigateTo(vm);
    }
}
