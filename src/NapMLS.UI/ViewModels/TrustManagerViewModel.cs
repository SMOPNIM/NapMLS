using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NapMLS.Core;
using NapMLS.Core.Services;
using NapMLS.UI.Services;

namespace NapMLS.UI.ViewModels;

/// <summary>
/// P1d-3c: Trust manager — safety code display, add peers, manage verification.
/// </summary>
public partial class TrustManagerViewModel : ViewModelBase
{
    private readonly SqliteStorage _storage;
    private readonly string _mySafetyCode;
    private readonly byte[] _myFingerprint;
    private readonly MlsTransportBridge? _bridge;
    private readonly MlsService? _mls;

    public NavigationService Navigation { get; set; } = null!;

    // -- My safety code --
    [ObservableProperty]
    public partial string SafetyCode { get; set; }

    // -- Add peer form --
    [ObservableProperty]
    public partial string? NewQqNumber { get; set; }

    [ObservableProperty]
    public partial string? NewSafetyCode { get; set; }

    [ObservableProperty]
    public partial string? NewNickname { get; set; }

    [ObservableProperty]
    public partial string? AddErrorMessage { get; set; }

    // -- Peer list --
    public ObservableCollection<PeerViewModel> Peers { get; } = [];

    public TrustManagerViewModel(SqliteStorage storage, byte[] myFingerprint,
        MlsTransportBridge? bridge = null, MlsService? mls = null)
    {
        _storage = storage;
        _myFingerprint = myFingerprint;
        _bridge = bridge;
        _mls = mls;
        _mySafetyCode = SafetyCodeFormatter.Format(myFingerprint);
        SafetyCode = _mySafetyCode;

        LoadPeers();
    }

    private void LoadPeers()
    {
        Peers.Clear();
        foreach (var peer in _storage.ListPeers())
        {
            Peers.Add(new PeerViewModel
            {
                QqNumber = peer.QqNumber,
                Nickname = peer.Nickname ?? peer.QqNumber,
                SafetyCode = peer.SafetyCode,
                IsVerified = peer.IsVerified,
                HasKeyPackage = peer.KeyPackage != null,
            });
        }
    }

    [RelayCommand]
    private void AddPeer()
    {
        AddErrorMessage = null;

        if (string.IsNullOrWhiteSpace(NewQqNumber))
        {
            AddErrorMessage = "请输入 QQ 号";
            return;
        }

        if (string.IsNullOrWhiteSpace(NewSafetyCode))
        {
            AddErrorMessage = "请输入对方安全码";
            return;
        }

        if (!SafetyCodeFormatter.IsValid(NewSafetyCode))
        {
            AddErrorMessage = "安全码格式不正确，应为 NAPMLS-XXXX-XXXX-XXXX-XXXX";
            return;
        }

        // Check if already exists
        if (_storage.GetPeer(NewQqNumber) != null)
        {
            AddErrorMessage = $"QQ 号 {NewQqNumber} 已在信任列表中";
            return;
        }

        // Parse safety code to fingerprint bytes
        var cleanCode = NewSafetyCode.Replace("NAPMLS-", "").Replace("-", "");
        var fingerprint = Convert.FromHexString(cleanCode);

        _storage.UpsertPeer(new TrustedPeer
        {
            QqNumber = NewQqNumber,
            Fingerprint = fingerprint,
            SafetyCode = NewSafetyCode.ToUpperInvariant(),
            Nickname = NewNickname,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });

        // Auto-send our KP to the new peer so they can add us to groups
        if (_bridge == null || !_bridge.HasSentKeyPackageTo(NewQqNumber))
        {
            _ = SendMyKeyPackageToAsync(NewQqNumber);
        }

        NewQqNumber = null;
        NewSafetyCode = null;
        NewNickname = null;
        LoadPeers();
    }

    [RelayCommand]
    private void RemovePeer(string qqNumber)
    {
        _bridge?.RemoveFromKeyPackageTracking(qqNumber);
        _storage.DeletePeer(qqNumber);
        LoadPeers();
    }

    /// <summary>
    /// Generate and send our KeyPackage to a peer via NapCat private message.
    /// The peer's bridge will auto-reply with its own KP (see MlsTransportBridge Fix 1).
    /// </summary>
    [RelayCommand]
    private async Task RequestKeyPackageAsync(string qqNumber)
    {
        var peer = _storage.GetPeer(qqNumber);
        if (peer == null) return;

        await SendMyKeyPackageToAsync(qqNumber);
    }

    private async Task SendMyKeyPackageToAsync(string qqNumber)
    {
        if (_mls == null || _bridge == null)
        {
            AddErrorMessage = "MLS 未初始化，无法发送密钥包";
            return;
        }

        try
        {
            AddErrorMessage = $"正在向 {qqNumber} 发送密钥包...";
            var kp = _mls.GenerateKeyPackage();
            if (kp == null)
            {
                AddErrorMessage = "生成密钥包失败";
                return;
            }

            await _bridge.SendKeyPackageAsync(long.Parse(qqNumber), kp);
            AddErrorMessage = $"已发送密钥包给 {qqNumber}，等待对方回应";
            LoadPeers();
        }
        catch (Exception ex)
        {
            AddErrorMessage = $"发送密钥包失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private void GoBack()
    {
        Navigation.GoBack();
    }
}

public partial class PeerViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string QqNumber { get; set; } = "";

    [ObservableProperty]
    public partial string Nickname { get; set; } = "";

    [ObservableProperty]
    public partial string SafetyCode { get; set; } = "";

    [ObservableProperty]
    public partial bool IsVerified { get; set; }

    [ObservableProperty]
    public partial bool HasKeyPackage { get; set; }

    public string StatusText => IsVerified ? "已验证" : "待验证";
    public string StatusColor => IsVerified ? "#4CAF50" : "#FFC107";
}
