using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NapMLS.UI.Services;

namespace NapMLS.UI.ViewModels;

/// <summary>
/// P1d-4: Chat page.
/// Shows messages for a single MLS group with encryption status.
/// </summary>
public partial class ChatViewModel : ViewModelBase
{
    private readonly NavigationService _navigation;

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

    public ChatViewModel(NavigationService navigation)
    {
        _navigation = navigation;
    }

    [RelayCommand]
    private void GoBack()
    {
        _navigation.GoBack();
    }

    [RelayCommand]
    private void SendMessage()
    {
        if (string.IsNullOrWhiteSpace(InputText)) return;

        Messages.Add(new MessageViewModel
        {
            SenderName = "我",
            Text = InputText,
            Timestamp = DateTime.Now,
            IsOwn = true,
            Status = "已加密发送",
            IsEncrypted = true,
        });

        LastMessagePreview = InputText.Length > 30
            ? InputText[..30] + "..."
            : InputText;

        InputText = "";
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
