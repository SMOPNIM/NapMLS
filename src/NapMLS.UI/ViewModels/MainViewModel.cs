using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NapMLS.UI.Services;

namespace NapMLS.UI.ViewModels;

/// <summary>
/// P1d-1: Main window ViewModel with navigation.
/// Manages page navigation and global state.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    public NavigationService Navigation { get; } = new();

    [ObservableProperty]
    public partial bool ShowRiskWarning { get; set; } = true;

    [ObservableProperty]
    public partial string WindowTitle { get; set; } = "NapMLS - 加密子群";

    public MainViewModel()
    {
        // Start with risk warning
        var riskWarning = new RiskWarningViewModel(OnRiskAccepted);
        Navigation.NavigateTo(riskWarning);
    }

    private void OnRiskAccepted()
    {
        ShowRiskWarning = false;
        Navigation.Clear();
        Navigation.NavigateTo(new SetupViewModel());
    }
}
