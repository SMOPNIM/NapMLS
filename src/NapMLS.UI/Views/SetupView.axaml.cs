using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NapMLS.UI.ViewModels;

namespace NapMLS.UI.Views;

public partial class SetupView : UserControl
{
    public SetupView()
    {
        InitializeComponent();
    }

    private async void CopyButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SetupViewModel vm && vm.SafetyCode != null)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard != null)
            {
                var data = new DataTransfer();
                data.Add(DataTransferItem.CreateText(vm.SafetyCode));
                await topLevel.Clipboard.SetDataAsync(data);
            }
        }
    }
}
