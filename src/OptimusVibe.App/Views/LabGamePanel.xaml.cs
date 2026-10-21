using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OptimusVibe.App.ViewModels;

namespace OptimusVibe.App.Views;

public partial class LabGamePanel : UserControl
{
    private LabGameViewModel? _vm;

    public LabGamePanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as LabGameViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        };
    }

    /// <summary>"More" opened from its button: the first action in it takes focus. Closed (B, or the button) while focus was inside: back to More.</summary>
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LabGameViewModel.IsMoreOpen) || _vm is null) return;
        var open = _vm.IsMoreOpen;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!IsVisible) return;
            if (open)
            {
                if (MoreButton.IsKeyboardFocused && FirstAction(MorePanel) is { } first) first.Focus();
            }
            else if (Keyboard.FocusedElement is not DependencyObject focused || IsInside(focused, MorePanel))
            {
                if (MoreButton.IsVisible) MoreButton.Focus();
            }
        });
    }

    private static Button? FirstAction(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button { IsVisible: true, IsEnabled: true, Focusable: true } b) return b;
            if (child is UIElement { IsVisible: false }) continue;
            if (FirstAction(child) is { } found) return found;
        }
        return null;
    }

    private static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (var d = element; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (ReferenceEquals(d, container)) return true;
        return false;
    }
}
