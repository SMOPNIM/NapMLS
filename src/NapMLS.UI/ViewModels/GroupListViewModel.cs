using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NapMLS.Core;
using NapMLS.UI.Models;
using NapMLS.UI.Services;

namespace NapMLS.UI.ViewModels;

/// <summary>
/// P1d-3e: Group list page with real data.
/// Shows MLS groups from Rust side + C# binding metadata.
/// </summary>
public partial class GroupListViewModel : ViewModelBase
{
    private readonly AppConfig _config;
    private readonly SqliteStorage _storage;
    private readonly byte[] _fingerprint;
    private readonly MlsService? _mls;

    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    public partial string ConnectionStatus { get; set; } = "NapCat 未连接";

    [ObservableProperty]
    public partial string WelcomeText { get; set; }

    public ObservableCollection<GroupItemViewModel> Groups { get; } = [];

    public GroupListViewModel(AppConfig config, SqliteStorage storage, byte[] fingerprint, MlsService? mls)
    {
        _config = config;
        _storage = storage;
        _fingerprint = fingerprint;
        _mls = mls;
        WelcomeText = $"欢迎, {config.IdentityUsername ?? "用户"}";
        LoadGroups();
    }

    private void LoadGroups()
    {
        Groups.Clear();

        if (_mls == null) return;

        var mlsGroups = _mls.ListGroups();
        foreach (var g in mlsGroups)
        {
            var binding = _storage.GetGroupBinding(Convert.FromHexString(g.group_id));
            long qqGroupId = 0;
            if (binding != null && long.TryParse(binding.QqGroupId, out var parsed))
                qqGroupId = parsed;

            Groups.Add(new GroupItemViewModel
            {
                GroupName = g.name,
                QqGroupId = qqGroupId,
                MemberCount = 1,
                SyncState = "已同步",
                LastActivity = $"Epoch {g.epoch}",
            });
        }

        if (Groups.Count == 0)
        {
            Groups.Add(new GroupItemViewModel
            {
                GroupName = "暂无子群，点击 + 新建 创建",
                QqGroupId = 0,
                MemberCount = 0,
                SyncState = "",
                LastActivity = "",
            });
        }
    }

    [RelayCommand]
    private void CreateGroup()
    {
        var peers = _storage.ListPeers();
        var selectablePeers = peers.Select(p => new SelectablePeer
        {
            QqNumber = p.QqNumber,
            Nickname = p.Nickname ?? p.QqNumber,
            SafetyCode = p.SafetyCode,
        }).ToList();

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NapMLS");
        var dbPath = Path.Combine(dir, "napmls.db");

        var wizard = new CreateSubgroupViewModel(_storage, dbPath, Navigation)
        {
            AvailablePeers = { },
        };
        foreach (var sp in selectablePeers)
            wizard.AvailablePeers.Add(sp);

        Navigation.NavigateTo(wizard);
    }

    [RelayCommand]
    private void OpenTrustManager()
    {
        Navigation.NavigateTo(new TrustManagerViewModel(_storage, _fingerprint));
    }

    public NavigationService Navigation { get; set; } = null!;
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
