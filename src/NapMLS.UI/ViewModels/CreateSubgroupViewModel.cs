using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NapMLS.Core;
using NapMLS.Core.Services;
using NapMLS.UI.Services;

namespace NapMLS.UI.ViewModels;

/// <summary>
/// P1d-3d: 4-step wizard for creating an MLS subgroup.
///   Step 1: Select QQ group
///   Step 2: Select trusted members
///   Step 3: Real KeyPackage exchange via FFI
///   Step 4: Confirm & create + AddMembers + send Welcome
/// </summary>
public partial class CreateSubgroupViewModel : ViewModelBase
{
    private readonly SqliteStorage _storage;
    private readonly string _dbPath;
    private readonly NavigationService _navigation;
    private readonly MlsService? _mls;
    private MlsTransportBridge? _bridge;
    private Action? _onGroupCreated;

    // Collected peer KeyPackages for AddMembers
    private readonly Dictionary<string, byte[]> _collectedKeyPackages = new();

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

        // Real QQ groups loaded via bridge — call LoadGroupsAsync after construction
    }

    /// <summary>Set the bridge reference for sending KP/Welcome via NapCat and fetching real groups.</summary>
    public void SetBridge(MlsTransportBridge? bridge)
    {
        _bridge = bridge;
        _ = LoadGroupsAsync();
    }

    /// <summary>Set callback to refresh group list after creation.</summary>
    public void SetRefreshCallback(Action? callback) => _onGroupCreated = callback;

    private async Task LoadGroupsAsync()
    {
        if (_bridge == null) return;
        try
        {
            var groups = await _bridge.GetGroupListAsync();
            QqGroups.Clear();
            foreach (var (gid, name) in groups)
                QqGroups.Add(new QqGroupOption { QqGroupId = gid, Name = name });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"获取群列表失败: {ex.Message}";
        }
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
    private async Task NextStep()
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
            await StartKeyPackageExchangeAsync();
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
            if (groupId == null)
            {
                ErrorMessage = "创建群组失败";
                return;
            }

            _storage.UpsertGroupBinding(new Core.GroupBinding
            {
                GroupId = Convert.FromHexString(groupId),
                QqGroupId = SelectedQqGroup.QqGroupId.ToString(),
                DisplayName = GroupName,
            });

            // Register group in bridge hashMap so incoming messages can be routed
            _bridge?.RegisterGroup(groupId, SelectedQqGroup.QqGroupId);

            // Add members with collected KeyPackages
            var kps = _collectedKeyPackages.Values.ToArray();
            if (kps.Length > 0)
            {
                var welcomeBytes = _mls.AddMembers(groupId, kps);
                if (welcomeBytes != null && _bridge != null)
                {
                    // Send Welcome to each selected member via private chat
                    foreach (var peer in SelectedMembers)
                    {
                        if (long.TryParse(peer.QqNumber, out var peerQq))
                        {
                            await _bridge.SendWelcomeAsync(peerQq, SelectedQqGroup.QqGroupId, welcomeBytes);
                            Console.WriteLine($"[Wizard] Sent Welcome to {peer.Nickname} ({peerQq})");
                        }
                    }
                }
            }

            _onGroupCreated?.Invoke();
            _navigation.GoBack();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"创建失败: {ex.Message}";
        }
    }

    private async Task StartKeyPackageExchangeAsync()
    {
        IsExchangeRunning = true;
        try
        {
            _collectedKeyPackages.Clear();
            ExchangeTotal = SelectedMembers.Count;
            ExchangeProgress = 0;

            foreach (var peer in SelectedMembers)
            {
            ExchangeStatus = $"正在与 {peer.Nickname} ({peer.QqNumber}) 交换密钥包...";

            // Check if we already have this peer's KP from a previous exchange
            var existingPeer = _storage.GetPeer(peer.QqNumber);
            if (existingPeer?.KeyPackage != null)
            {
                _collectedKeyPackages[peer.QqNumber] = existingPeer.KeyPackage;
                peer.KeyPackageReady = true;
                ExchangeProgress++;
                ExchangeStatus = $"{peer.Nickname} 密钥包已就绪 (缓存)";
                await Task.Delay(100);
                continue;
            }

            // Generate our own KP and optionally send it to the peer
            var myKp = _mls?.GenerateKeyPackage();
            if (myKp != null && _bridge != null && long.TryParse(peer.QqNumber, out var peerQq))
            {
                await _bridge.SendKeyPackageAsync(peerQq, myKp);
                ExchangeStatus = $"已发送密钥包给 {peer.Nickname}，等待回复...";
            }
            else
            {
                ExchangeStatus = $"等待 {peer.Nickname} 发送密钥包...";
            }

            // Poll storage for up to 15 seconds
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
                var peerData = _storage.GetPeer(peer.QqNumber);
                if (peerData?.KeyPackage != null)
                {
                    _collectedKeyPackages[peer.QqNumber] = peerData.KeyPackage;
                    peer.KeyPackageReady = true;
                    ExchangeStatus = $"{peer.Nickname} 密钥包已就绪";
                    break;
                }
            }

            ExchangeProgress++;

            if (!peer.KeyPackageReady)
            {
                ExchangeStatus = $"{peer.Nickname} 密钥包超时（15秒），该成员将被跳过";
                await Task.Delay(1000); // Show timeout message briefly
            }
        }

        var readyCount = _collectedKeyPackages.Count;
        ExchangeStatus = readyCount == SelectedMembers.Count
            ? $"密钥交换完成，共 {readyCount} 位成员"
            : $"密钥交换完成，{readyCount}/{SelectedMembers.Count} 位成员就绪（未就绪成员将被跳过）";
        CurrentStep = 4;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"密钥交换失败: {ex.Message}";
            Console.WriteLine($"[Wizard] KeyPackage exchange failed: {ex}");
        }
        finally
        {
            IsExchangeRunning = false;
        }
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
