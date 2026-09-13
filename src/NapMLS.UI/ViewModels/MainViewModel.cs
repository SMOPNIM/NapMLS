using CommunityToolkit.Mvvm.ComponentModel;
using NapMLS.UI.Models;
using NapMLS.UI.Services;

namespace NapMLS.UI.ViewModels;

/// <summary>
/// P1d-2: Main window ViewModel with startup state machine.
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

    public MainViewModel()
    {
        _config = AppConfigService.Load();
        RunStartupStateMachine();
    }

    private void RunStartupStateMachine()
    {
        if (!_config.RiskWarningAccepted)
        {
            // Never accepted risk → show risk warning first
            Navigation.NavigateTo(new RiskWarningViewModel(OnRiskAccepted));
        }
        else if (string.IsNullOrEmpty(_config.IdentitySafetyCode))
        {
            // Accepted but no identity yet → setup step 2
            Navigation.NavigateTo(new SetupViewModel(_config, OnSetupCompleted));
        }
        else
        {
            // Everything ready → go to groups
            Navigation.NavigateTo(new GroupListViewModel(_config));
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
        Navigation.NavigateTo(new GroupListViewModel(_config));
    }
}
