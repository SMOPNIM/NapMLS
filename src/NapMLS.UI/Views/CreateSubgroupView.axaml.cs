using Avalonia.Controls;
using Avalonia.Interactivity;
using NapMLS.UI.ViewModels;

namespace NapMLS.UI.Views;

public partial class CreateSubgroupView : UserControl
{
    public CreateSubgroupView()
    {
        InitializeComponent();
    }

    private void OnQqGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is QqGroupOption group
            && DataContext is CreateSubgroupViewModel vm)
        {
            vm.SelectedQqGroup = group;
        }
    }
}
