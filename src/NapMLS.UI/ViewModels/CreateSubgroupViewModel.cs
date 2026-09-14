using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NapMLS.Core;
using NapMLS.UI.Services;

namespace NapMLS.UI.ViewModels;

/// <summary>
/// P1d-3d: 4-step wizard for creating an MLS subgroup.
///   Step 1: Select QQ group
///   Step 2: Select trusted members
///   Step 3: Mock KeyPackage exchange
///   Step 4: Confirm & create
/// </summary>
public partial class CreateSubgroupViewModel : ViewModelBase
{
    private readonly SqliteStorage _storage;
    private readonly string _dbPath;
    private readonly NavigationService _navigation;
    private readonly MlsService? _mls;

    [ObservableProperty]
    public partial int CurrentStep { get; set; } = 1;

    [ObservableProperty]
    public partial string StepTitle { get; set; } = "选择 QQ 群";

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    // -- Step 1: QQ group selection --
    public ObservableCollection<QqGroupOption> QqGroups { get; } = [];

    [ObservableProperty]
    public partial QqGroupOption? SelectedQqGroup { get; set; }

    // -- Step 2: Member selection --
    public ObservableCollection<SelectablePeer> AvailablePeers { get; } = [];

    // -- Step 3: KeyPackage exchange --
    [ObservableProperty]
    public partial string ExchangeStatus { get; set; } = "";

    [ObservableProperty]
    public partial int ExchangeProgress { get; set; }

    [ObservableProperty]
    public partial int ExchangeTotal { get; set; }

    [ObservableProperty]
    public partial bool IsExchangeRunning { get; set; }

    // -- Step 4: Confirm --
    [ObservableProperty]
    public partial string GroupName { get; set; } = "";

    public ObservableCollection<SelectablePeer> SelectedMembers { get; } = [];

    public CreateSubgroupViewModel(SqliteStorage storage, string dbPath, NavigationService navigation, MlsService? mls = null)
    {
        _storage = storage;
        _dbPath = dbPath;
        _navigation = navigation;
        _mls = mls;

        // Mock QQ groups — P1d-5 replace with real NapCat data
        QqGroups.Add(new QqGroupOption { QqGroupId = 10001, Name = "技术交流群" });
        QqGroups.Add(new QqGroupOption { QqGroupId = 10002, Name = "项目协作群" });
        QqGroups.Add(new QqGroupOption { QqGroupId = 10003, Name = "核心团队" });
    }

    partial void OnCurrentStepChanged(int value)
    {
        StepTitle = value switch
        {
            1 => "选择 QQ 群",
            2 => "选择成员",
            3 => "密钥交换",
            4 => "确认创建",
            _ => ""
        };
    }

    [RelayCommand]
    private void NextStep()
    {
        if (CurrentStep == 1 && SelectedQqGroup == null) return;

        if (CurrentStep == 2)
        {
            SelectedMembers.Clear();
            foreach (var p in AvailablePeers.Where(p => p.IsSelected))
                SelectedMembers.Add(p);

            if (SelectedMembers.Count == 0) return;

            GroupName = $"{SelectedQqGroup!.Name} - 加密子群";
        }

        if (CurrentStep == 3)
        {
            StartKeyPackageExchange();
            return;
        }

        if (CurrentStep < 4)
            CurrentStep++;
    }

    [RelayCommand]
    private void PrevStep()
    {
        if (CurrentStep > 1)
            CurrentStep--;
    }

    [RelayCommand]
    private void Cancel()
    {
        _navigation.GoBack();
    }

    [RelayCommand]
    private async Task CreateGroupAsync()
    {
        if (string.IsNullOrWhiteSpace(GroupName) || SelectedQqGroup == null)
            return;

        if (_mls == null || !_mls.HasIdentity)
        {
            ErrorMessage = "MLS 服务未初始化或身份不存在";
            return;
        }

        try
        {
            var groupId = _mls.CreateGroup(GroupName);
            if (groupId != null)
            {
                // Save binding for UI metadata
                _storage.UpsertGroupBinding(new Core.GroupBinding
                {
                    GroupId = Convert.FromHexString(groupId),
                    QqGroupId = SelectedQqGroup.QqGroupId.ToString(),
                    DisplayName = GroupName,
                });

                await Task.Delay(100);
                _navigation.GoBack();
            }
            else
            {
                ErrorMessage = "创建群组失败";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"创建失败: {ex.Message}";
        }
    }

    private async void StartKeyPackageExchange()
    {
        IsExchangeRunning = true;
        ExchangeTotal = SelectedMembers.Count;
        ExchangeProgress = 0;

        foreach (var peer in AvailablePeers.Where(p => p.IsSelected))
        {
            ExchangeStatus = $"正在与 {peer.Nickname} ({peer.QqNumber}) 交换密钥包...";
            await Task.Delay(500); // Mock network delay
            peer.KeyPackageReady = true;
            ExchangeProgress++;
        }

        ExchangeStatus = $"密钥交换完成，共 {ExchangeTotal} 位成员";
        IsExchangeRunning = false;
        CurrentStep = 4;
    }
}

public partial class QqGroupOption : ObservableObject
{
    [ObservableProperty]
    public partial long QqGroupId { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    public override string ToString() => $"{Name} ({QqGroupId})";
}

public partial class SelectablePeer : ObservableObject
{
    [ObservableProperty]
    public partial string QqNumber { get; set; } = "";

    [ObservableProperty]
    public partial string Nickname { get; set; } = "";

    [ObservableProperty]
    public partial string SafetyCode { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool KeyPackageReady { get; set; }

    public string DisplayText => $"{Nickname} ({QqNumber})";
}
