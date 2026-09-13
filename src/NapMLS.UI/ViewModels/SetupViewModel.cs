using CommunityToolkit.Mvvm.ComponentModel;

namespace NapMLS.UI.ViewModels;

/// <summary>
/// P1d-2: Setup page — NapCat config + identity generation.
/// Placeholder for now.
/// </summary>
public partial class SetupViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial string NapCatHost { get; set; } = "127.0.0.1";

    [ObservableProperty]
    public partial int NapCatPort { get; set; } = 8080;

    [ObservableProperty]
    public partial string? NapCatToken { get; set; }

    [ObservableProperty]
    public partial string? MyFingerprint { get; set; }

    [ObservableProperty]
    public partial string? MySafetyCode { get; set; }

    [ObservableProperty]
    public partial bool IsIdentityGenerated { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "";
}
