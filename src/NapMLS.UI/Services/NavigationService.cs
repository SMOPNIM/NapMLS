using CommunityToolkit.Mvvm.ComponentModel;
using NapMLS.UI.ViewModels;

namespace NapMLS.UI.Services;

/// <summary>
/// Simple navigation service. Maintains a stack of ViewModels.
/// P1d: basic; P2: add back navigation.
/// </summary>
public partial class NavigationService : ObservableObject
{
    private readonly Stack<ViewModelBase> _stack = new();

    [ObservableProperty]
    public partial ViewModelBase? CurrentView { get; set; }

    public bool CanGoBack => _stack.Count > 0;

    public void NavigateTo(ViewModelBase viewModel)
    {
        if (CurrentView != null)
            _stack.Push(CurrentView);
        CurrentView = viewModel;
        OnPropertyChanged(nameof(CanGoBack));
    }

    public void GoBack()
    {
        if (_stack.Count > 0)
        {
            CurrentView = _stack.Pop();
            OnPropertyChanged(nameof(CanGoBack));
        }
    }

    public void Clear()
    {
        _stack.Clear();
        OnPropertyChanged(nameof(CanGoBack));
    }
}
