using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NapMLS.UI.ViewModels;

/// <summary>
/// P1d-4: Chat page.
/// Shows messages for a single MLS group.
/// </summary>
public partial class ChatViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial string GroupName { get; set; } = "";

    [ObservableProperty]
    public partial string? GroupHash { get; set; }

    [ObservableProperty]
    public partial string InputText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSending { get; set; }

    public ObservableCollection<MessageViewModel> Messages { get; } = [];

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
            Status = "已发送"
        });

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
