using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OptimusVibe.App.Views;

public partial class GameDetailView : UserControl
{
    public GameDetailView()
    {
        InitializeComponent();
        // The tab strip keeps arrow keys inside itself (TabPanel cycles), so Up / Down from a tab header would go nowhere.
        Tabs.PreviewKeyDown += (_, e) =>
        {
            if (e.OriginalSource is not TabItem) return;
            if ((e.Key == Key.Down && FocusSelectedContent()) || (e.Key == Key.Up && FocusAboveTabs())) e.Handled = true;
        };
    }

    /// <summary>After LB/RB, put focus on the selected tab header so D-pad Down enters its content.</summary>
    public void FocusSelectedTab()
    {
        if (Tabs.ItemContainerGenerator.ContainerFromIndex(Tabs.SelectedIndex) is TabItem tab) tab.Focus();
    }

    /// <summary>
    /// D-pad on a Recommended / Manual saves tab header: Down moves into that tab's first control (issue #50), Up to the last control above the
    /// tabs (the per-game toggles, else the game lab).
    /// </summary>
    public bool TryMove(UIElement focused, FocusNavigationDirection direction)
    {
        if (focused is not TabItem tab || ItemsControl.ItemsControlFromItemContainer(tab) != Tabs) return false;
        return direction switch
        {
            FocusNavigationDirection.Down => FocusSelectedContent(),
            FocusNavigationDirection.Up => FocusAboveTabs(),
            _ => false,
        };
    }

    private bool FocusSelectedContent() =>
        Tabs.Template?.FindName("PART_SelectedContentHost", Tabs) is DependencyObject host && Focus(FirstFocusable(host));

    private bool FocusAboveTabs() => Focus(LastFocusable(StatusBlock));

    private static bool Focus(UIElement? target)
    {
        if (target is null || !target.Focus()) return false;
        (target as FrameworkElement)?.BringIntoView();
        return true;
    }

    private static bool CanFocus(UIElement el) => el.Focusable && el.IsEnabled && el is Control;

    private static UIElement? FirstFocusable(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            if (VisualTreeHelper.GetChild(node, i) is not UIElement child || !child.IsVisible) continue;
            if (CanFocus(child)) return child;
            if (FirstFocusable(child) is { } inner) return inner;
        }
        return null;
    }

    private static UIElement? LastFocusable(DependencyObject node)
    {
        for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
        {
            if (VisualTreeHelper.GetChild(node, i) is not UIElement child || !child.IsVisible) continue;
            if (CanFocus(child)) return child;
            if (LastFocusable(child) is { } inner) return inner;
        }
        return null;
    }
}
