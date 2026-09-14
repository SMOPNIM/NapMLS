using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NapMLS.UI.Services;

namespace NapMLS.UI.ViewModels;

/// <summary>
/// P1d-4: Chat page with real MLS encrypt/decrypt.
/// Shows messages for a single MLS group.
/// </summary>
public partial class ChatViewModel : ViewModelBase
{
    private readonly NavigationService _navigation;
    private readonly MlsService? _mls;

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

    public ChatViewModel(NavigationService navigation, MlsService? mls = null)
    {
        _navigation = navigation;
        _mls = mls;
    }

    [RelayCommand]
    private void GoBack()
    {
        _navigation.GoBack();
    }

    [RelayCommand]
    private void SendMessage()
    {
        if (string.IsNullOrWhiteSpace(InputText) || GroupId == null || _mls == null)
        {
            // Fallback: send unencrypted if no MLS
            AddMessage("我", InputText, isOwn: true, encrypted: false, status: "未加密");
            InputText = "";
            return;
        }

        try
        {
            var cipher = _mls.Encrypt(GroupId, InputText);
            if (cipher != null)
            {
                // Own message: display plaintext locally (MLS returns null on self-decrypt)
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

        LastMessagePreview = InputText.Length > 30
            ? InputText[..30] + "..."
            : InputText;
        InputText = "";
    }

    /// <summary>
    /// Receive and decrypt a message from the wire.
    /// Called by NapCat transport when a group message arrives.
    /// </summary>
    public void ReceiveMessage(string senderName, byte[] ciphertext)
    {
        if (GroupId == null || _mls == null)
        {
            AddMessage(senderName, "[无法解密]", isOwn: false, encrypted: true, status: "解密失败");
            return;
        }

        try
        {
            var plaintext = _mls.Decrypt(GroupId, ciphertext);
            if (plaintext != null)
            {
                AddMessage(senderName, plaintext, isOwn: false, encrypted: true, status: "已解密");
            }
            else
            {
                AddMessage(senderName, "[密文无法解密]", isOwn: false, encrypted: true, status: "解密失败");
            }
        }
        catch
        {
            AddMessage(senderName, "[密文无法解密]", isOwn: false, encrypted: true, status: "解密失败");
        }
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
