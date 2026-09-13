using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NapMLS.UI.Models;

namespace NapMLS.UI.ViewModels;

/// <summary>
/// P1d-3: Group list page.
/// Shows MLS groups bound to QQ groups.
/// </summary>
public partial class GroupListViewModel : ViewModelBase
{
    private readonly AppConfig _config;

    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    public partial string ConnectionStatus { get; set; } = "NapCat 未连接";

    [ObservableProperty]
    public partial string WelcomeText { get; set; }

    public ObservableCollection<GroupItemViewModel> Groups { get; } = [];

    public GroupListViewModel(AppConfig config)
    {
        _config = config;
        WelcomeText = $"欢迎, {config.IdentityUsername ?? "用户"}";
    }

    [RelayCommand]
    private void CreateGroup()
    {
        // P1d-3: open create subgroup dialog
    }
}

/// <summary>
/// A single group in the list.
/// </summary>
public partial class GroupItemViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial string GroupName { get; set; } = "";

    [ObservableProperty]
    public partial long QqGroupId { get; set; }

    [ObservableProperty]
    public partial int MemberCount { get; set; }

    [ObservableProperty]
    public partial string SyncState { get; set; } = "已同步";

    [ObservableProperty]
    public partial string LastActivity { get; set; } = "";
}
