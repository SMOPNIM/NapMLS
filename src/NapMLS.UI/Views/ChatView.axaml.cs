using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NapMLS.UI.ViewModels;

namespace NapMLS.UI.Views;

public partial class ChatView : UserControl
{
    public ChatView()
    {
        InitializeComponent();
    }

    private void TextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ChatViewModel vm)
        {
            vm.SendMessageCommand.Execute(null);
        }
    }
}
