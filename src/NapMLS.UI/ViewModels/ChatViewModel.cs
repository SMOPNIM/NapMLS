using System.Collections.ObjectModel;
using System.Text;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NapMLS.Core;
using NapMLS.UI.Services;

namespace NapMLS.UI.ViewModels;

/// <summary>
/// Chat page with real MLS encrypt/decrypt via MlsTransportBridge.
/// Shows messages for a single MLS group.
/// </summary>
public partial class ChatViewModel : ViewModelBase
{
    private readonly NavigationService _navigation;
    private readonly MlsService? _mls;
    private readonly MlsTransportBridge? _bridge;
    private readonly MessageBus? _bus;
    private IDisposable? _messageSubscription;
    private long _qqGroupId;

    [ObservableProperty]
    public partial string GroupName { get; set; } = "";

    [ObservableProperty]
    public partial string? GroupId { get; set; }

    [ObservableProperty]
    public partial int Epoch { get; set; }

    [ObservableProperty]
    public partial string EncryptionStatus { get; set; } = "MLS 加密中";

    [ObservableProperty]
    public partial string InputText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSending { get; set; }

    [ObservableProperty]
    public partial string? LastMessagePreview { get; set; }

    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    private string? _currentGroupHash;

    public ChatViewModel(NavigationService navigation, MlsService? mls = null, MlsTransportBridge? bridge = null, MessageBus? bus = null, long qqGroupId = 0)
    {
        _navigation = navigation;
        _mls = mls;
        _bridge = bridge;
        _bus = bus;
        _qqGroupId = qqGroupId;
    }

    partial void OnGroupIdChanged(string? value)
    {
        _messageSubscription?.Dispose();
        _messageSubscription = null;
        _currentGroupHash = null;

        if (value != null && _bus != null)
        {
            _currentGroupHash = MessageChunker.ComputeGroupHash(Convert.FromHexString(value));
            _messageSubscription = _bus.Subscribe<MessageReceivedEvent>(evt =>
            {
                if (evt.GroupHash != _currentGroupHash)
                    return;

                var senderName = evt.Sender.ToString();
                var plaintext = Encoding.UTF8.GetString(evt.Plaintext);

                Dispatcher.UIThread.Post(() =>
                {
                    AddMessage(senderName, plaintext, isOwn: false, encrypted: true, status: "已解密");
                    Epoch = (int)evt.Epoch;
                });
            });
        }
    }

    [RelayCommand]
    private void GoBack()
    {
        _navigation.GoBack();
    }

    [RelayCommand]
    private async Task SendMessage()
    {
        if (string.IsNullOrWhiteSpace(InputText) || GroupId == null)
        {
            AddMessage("我", InputText, isOwn: true, encrypted: false, status: "未加密");
            InputText = "";
            return;
        }

        if (_bridge != null && _qqGroupId != 0)
        {
            // Real NapCat transport
            try
            {
                IsSending = true;
                var ok = await _bridge.SendAsync(_qqGroupId, GroupId, InputText);
                if (ok)
                {
                    AddMessage("我", InputText, isOwn: true, encrypted: true, status: "已加密发送");
                }
                else
                {
                    AddMessage("我", InputText, isOwn: true, encrypted: false, status: "发送失败");
                }
            }
            catch (Exception ex)
            {
                AddMessage("我", InputText, isOwn: true, encrypted: false, status: $"错误: {ex.Message}");
            }
            finally
            {
                IsSending = false;
            }
        }
        else if (_mls != null)
        {
            // FFI-only mode (testing without NapCat)
            try
            {
                var cipher = _mls.Encrypt(GroupId, InputText);
                if (cipher != null)
                {
                    AddMessage("我", InputText, isOwn: true, encrypted: true, status: "已加密发送");
                }
                else
                {
                    AddMessage("我", InputText, isOwn: true, encrypted: false, status: "加密失败");
                }
            }
            catch (Exception ex)
            {
                AddMessage("我", InputText, isOwn: true, encrypted: false, status: $"错误: {ex.Message}");
            }
        }
        else
        {
            AddMessage("我", InputText, isOwn: true, encrypted: false, status: "未加密");
        }

        LastMessagePreview = InputText.Length > 30
            ? InputText[..30] + "..."
            : InputText;
        InputText = "";
    }

    private void AddMessage(string sender, string text, bool isOwn, bool encrypted, string status)
    {
        Messages.Add(new MessageViewModel
        {
            SenderName = sender,
            Text = text,
            Timestamp = DateTime.Now,
            IsOwn = isOwn,
            IsEncrypted = encrypted,
            Status = status,
        });
    }

    public void Dispose()
    {
        _messageSubscription?.Dispose();
    }
}

/// <summary>
/// A single message in the chat.
/// </summary>
public partial class MessageViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial string SenderName { get; set; } = "";

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial DateTime Timestamp { get; set; }

    [ObservableProperty]
    public partial bool IsOwn { get; set; }

    [ObservableProperty]
    public partial bool IsEncrypted { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";
}
