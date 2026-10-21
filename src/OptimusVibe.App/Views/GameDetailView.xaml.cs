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
        // The tab strip keeps arrow keys inside itself (TabPanel cycles), so Down from a tab header would go nowhere.
        Tabs.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && e.OriginalSource is TabItem && FocusSelectedContent()) e.Handled = true;
        };
    }

    /// <summary>Game lab and settings sync side by side when there is room (no dead space beside the lab), stacked when narrow.</summary>
    private const double SideBySideMinWidth = 1000;
    private bool? _sideBySide;

    private void OnStatusRowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= SideBySideMinWidth;
        if (_sideBySide == wide) return;
        _sideBySide = wide;
        StatusGap.Width = new GridLength(wide ? 16 : 0);
        StatusRight.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(StatusSide, wide ? 0 : 1);
        Grid.SetColumn(StatusSide, wide ? 2 : 0);
        if (wide) StatusSide.Margin = new Thickness(0);
        else StatusSide.SetResourceReference(MarginProperty, "BlockGapMd");
        LabPanel.HorizontalContentAlignment = wide ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        SyncCard.HorizontalAlignment = wide ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        SyncCard.MaxWidth = wide ? double.PositiveInfinity : 760;
    }

    /// <summary>After LB/RB, put focus on the selected tab header so D-pad Down enters its content.</summary>
    public void FocusSelectedTab()
    {
        if (Tabs.ItemContainerGenerator.ContainerFromIndex(Tabs.SelectedIndex) is TabItem tab) tab.Focus();
    }

    /// <summary>D-pad Down on a Recommended / Manual saves tab header moves into that tab's first control (issue #50).</summary>
    public bool TryMove(UIElement focused, FocusNavigationDirection direction) =>
        direction == FocusNavigationDirection.Down && focused is TabItem tab && ItemsControl.ItemsControlFromItemContainer(tab) == Tabs
        && FocusSelectedContent();

    private bool FocusSelectedContent()
    {
        if (Tabs.Template?.FindName("PART_SelectedContentHost", Tabs) is not DependencyObject host) return false;
        var target = FirstFocusable(host);
        if (target is null || !target.Focus()) return false;
        (target as FrameworkElement)?.BringIntoView();
        return true;
    }

    private static UIElement? FirstFocusable(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            if (VisualTreeHelper.GetChild(node, i) is not UIElement child || !child.IsVisible) continue;
            if (child.Focusable && child.IsEnabled && child is Control) return child;
            if (FirstFocusable(child) is { } inner) return inner;
        }
        return null;
    }
}
