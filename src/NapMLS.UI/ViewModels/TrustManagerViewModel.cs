using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NapMLS.Core;
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

    public TrustManagerViewModel(SqliteStorage storage, byte[] myFingerprint)
    {
        _storage = storage;
        _myFingerprint = myFingerprint;
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

        NewQqNumber = null;
        NewSafetyCode = null;
        NewNickname = null;
        LoadPeers();
    }

    [RelayCommand]
    private void RemovePeer(string qqNumber)
    {
        _storage.DeletePeer(qqNumber);
        LoadPeers();
    }

    /// <summary>
    /// Mock: receive a KeyPackage from a peer, verify fingerprint matches safety code.
    /// In P1d-5 this becomes real NapCat message handling.
    /// </summary>
    [RelayCommand]
    private void MockReceiveKeyPackage(string qqNumber)
    {
        var peer = _storage.GetPeer(qqNumber);
        if (peer == null) return;

        // P1d-3 mock: generate a fake KeyPackage
        // In real flow, this comes from the network
        var fakeKp = new byte[256];
        Random.Shared.NextBytes(fakeKp);

        // Compute fingerprint from fake KP (mock — in real flow, extract from KP)
        // For mock, we just mark as verified if safety code format is valid
        _storage.SetKeyPackage(qqNumber, fakeKp);
        _storage.SetVerified(qqNumber, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        LoadPeers();
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
