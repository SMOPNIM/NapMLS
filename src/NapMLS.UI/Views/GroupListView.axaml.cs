using Avalonia.Controls;
using Avalonia.Interactivity;
using NapMLS.UI.ViewModels;

namespace NapMLS.UI.Views;

public partial class GroupListView : UserControl
{
    public GroupListView()
    {
        InitializeComponent();
    }

    private void OnGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is GroupItemViewModel group
            && DataContext is GroupListViewModel vm)
        {
            vm.OpenGroupCommand.Execute(group);
        }
    }
}
