using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NapMLS.UI.ViewModels;

/// <summary>
/// P1d-1: Risk warning page shown on first launch.
/// User must acknowledge before proceeding.
/// </summary>
public partial class RiskWarningViewModel : ViewModelBase
{
    private readonly Action _onAccepted;

    [ObservableProperty]
    public partial bool IsAccepted { get; set; }

    public RiskWarningViewModel(Action onAccepted)
    {
        _onAccepted = onAccepted;
    }

    [RelayCommand]
    private void Accept()
    {
        IsAccepted = true;
        _onAccepted();
    }
}
