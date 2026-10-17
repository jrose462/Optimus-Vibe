using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OptimusVibe.App.Services;
using OptimusVibe.App.ViewModels;

namespace OptimusVibe.App.Views;

/// <summary>
/// Cover grid. Sizes tiles so whole columns fill the width and one full row fits the height, and does its own
/// row/column focus navigation (WPF's spatial guess skips tiles in ragged rows).
/// </summary>
public partial class LibraryView : UserControl
{
    private const double TargetSlot = 220; // ~5 columns at 1280 DIPs (Ally 7" at 150%)
    private const double CaptionHeight = 58;  // name + status line under the art
    private const double SlotGap = 26;
    private const double CompactTargetSlot = 168; // 7 columns at 1280 DIPs
    private const double CompactSlotGap = 10;
    private const double CompactRowMargin = 8;    // the tile's vertical margin

    public static readonly DependencyProperty TileWidthProperty =
        DependencyProperty.Register(nameof(TileWidth), typeof(double), typeof(LibraryView), new PropertyMetadata(200.0));
    public static readonly DependencyProperty ArtHeightProperty =
        DependencyProperty.Register(nameof(ArtHeight), typeof(double), typeof(LibraryView), new PropertyMetadata(300.0));
    public static readonly DependencyProperty SlotWidthProperty =
        DependencyProperty.Register(nameof(SlotWidth), typeof(double), typeof(LibraryView), new PropertyMetadata(226.0));

    public double TileWidth { get => (double)GetValue(TileWidthProperty); set => SetValue(TileWidthProperty, value); }
    public double ArtHeight { get => (double)GetValue(ArtHeightProperty); set => SetValue(ArtHeightProperty, value); }
    public double SlotWidth { get => (double)GetValue(SlotWidthProperty); set => SetValue(SlotWidthProperty, value); }

    public int Columns { get; private set; } = 5;

    public LibraryView()
    {
        InitializeComponent();
        TileList.SizeChanged += (_, _) => Layout();
        Scroller.SizeChanged += (_, _) => Layout();
        CompactTop.SizeChanged += (_, _) => Layout();
        FilterScroller.SizeChanged += (_, _) => Layout();
        DependencyPropertyDescriptor.FromProperty(Density.IsCompactProperty, typeof(LibraryView))
            .AddValueChanged(this, (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Layout));
        // the filter row scrolls sideways only: hand the wheel to the page so it still scrolls up and down over it
        FilterScroller.PreviewMouseWheel += (_, e) =>
        {
            if (e.Handled) return;
            e.Handled = true;
            Scroller.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = FilterScroller });
        };
        TileList.PreviewKeyDown += OnTilesKeyDown;
        TileList.MouseDoubleClick += OnTilesDoubleClick;
        Pills.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && ((!Compact && FocusSyncPills()) || FocusSelectedTile())) e.Handled = true;
        };
        SyncPills.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Up && !Compact && FocusPills()) e.Handled = true;
            else if (e.Key == Key.Down && FocusSelectedTile()) e.Handled = true;
        };
    }

    private LibraryViewModel? Vm => DataContext as LibraryViewModel;

    /// <summary>Compact: both filter rows are one row, so Up / Down skip the sync pills.</summary>
    private bool Compact => Density.GetIsCompact(this);

    private void Layout()
    {
        // List padding (14 each side) + a scroll bar's worth so tiles don't reflow when it appears.
        double avail = TileList.ActualWidth - 28 - 14;
        double viewport = Scroller.ActualHeight;
        if (avail <= 0 || viewport <= 0) return;

        bool compact = Density.GetIsCompact(this);
        double targetSlot = compact ? CompactTargetSlot : TargetSlot;
        double gap = compact ? CompactSlotGap : SlotGap;
        int cols = Math.Max(2, (int)Math.Floor(avail / targetSlot));
        double slot = Math.Floor(avail / cols);
        double tile = slot - gap;
        double art = tile * 1.5;                              // Steam portrait covers are 2:3
        if (compact)
        {
            // Names sit on the cover, so a row is just the art. Shorten the covers (cropped, not squashed) until two full rows fit
            // between the filters and the navigation, but never below 1.2:1.
            double top = 0;
            try { top = TileList.TransformToAncestor(PageGrid).Transform(new Point(0, 0)).Y + PageGrid.Margin.Top; }
            catch (InvalidOperationException) { /* not in the tree yet */ }
            double rowRoom = (viewport - top - 10) / 2 - CompactRowMargin;
            art = Math.Clamp(rowRoom, tile * 1.2, tile * 1.5);
        }
        else
        {
            double maxArt = viewport - CaptionHeight - 40;    // a whole tile always fits on screen
            if (art > maxArt && maxArt > 150)
            {
                art = maxArt;
                tile = Math.Floor(art / 1.5);
            }
        }
        Columns = cols;
        SlotWidth = slot;
        TileWidth = tile;
        ArtHeight = Math.Floor(art);
    }

    // ---------------------------------------------------------------- focus

    public bool FocusSelectedTile()
    {
        var vm = Vm;
        if (vm is null || TileList.Items.Count == 0) return false;
        var item = vm.SelectedTile ?? vm.Tiles.FirstOrDefault();
        if (item is null) return false;
        TileList.UpdateLayout();
        if (TileList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem lbi) return false;
        vm.SelectedTile = item;
        lbi.Focus();
        lbi.BringIntoView();
        return lbi.IsKeyboardFocused;
    }

    public bool FocusPills()
    {
        var vm = Vm;
        if (vm is null) return false;
        Scroller.ScrollToTop();
        var idx = vm.Filters.ToList().FindIndex(f => f.IsSelected);
        if (Pills.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, idx)) is ContentPresenter cp &&
            VisualTreeHelper.GetChildrenCount(cp) > 0 && VisualTreeHelper.GetChild(cp, 0) is Button b)
            return b.Focus();
        return false;
    }

    /// <summary>The "Settings sync" row: reached with the D-pad (down from the status pills, up from the first tile row).</summary>
    public bool FocusSyncPills()
    {
        var vm = Vm;
        if (vm is null) return false;
        var idx = vm.SyncFilters.ToList().FindIndex(f => f.IsSelected);
        if (SyncPills.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, idx)) is ContentPresenter cp &&
            VisualTreeHelper.GetChildrenCount(cp) > 0 && VisualTreeHelper.GetChild(cp, 0) is Button b)
        {
            b.BringIntoView();
            return b.Focus();
        }
        return false;
    }

    public bool IsTile(DependencyObject? d) =>
        d is ListBoxItem lbi && ItemsControl.ItemsControlFromItemContainer(lbi) == TileList;

    /// <summary>Row/column navigation over the wrap grid. Up from the first row goes to the filter pills.</summary>
    public bool TryMove(UIElement focused, FocusNavigationDirection dir)
    {
        if (IsInside(Pills, focused))
            return dir == FocusNavigationDirection.Down && ((!Compact && FocusSyncPills()) || FocusSelectedTile());
        if (IsInside(SyncPills, focused))
        {
            if (dir == FocusNavigationDirection.Up) return !Compact && FocusPills() || Compact;
            return dir == FocusNavigationDirection.Down && FocusSelectedTile();
        }
        if (!IsTile(focused)) return false;

        int count = TileList.Items.Count;
        int idx = TileList.ItemContainerGenerator.IndexFromContainer((ListBoxItem)focused);
        if (idx < 0) return false;
        int cols = Math.Max(1, Columns);
        int target = dir switch
        {
            FocusNavigationDirection.Left => idx % cols == 0 ? -1 : idx - 1,
            FocusNavigationDirection.Right => idx + 1 < count && (idx + 1) % cols != 0 ? idx + 1 : -1,
            FocusNavigationDirection.Up => idx - cols,
            FocusNavigationDirection.Down => idx + cols < count ? idx + cols
                : idx / cols < (count - 1) / cols ? count - 1   // ragged last row: land on its last tile
                : -1,
            _ => -1,
        };
        if (target < 0)
        {
            if (dir == FocusNavigationDirection.Up) return (!Compact && FocusSyncPills()) || FocusPills();
            return true; // edge of the grid: stay put rather than jumping somewhere unexpected
        }
        if (TileList.ItemContainerGenerator.ContainerFromIndex(target) is ListBoxItem next)
        {
            TileList.SelectedIndex = target;
            next.Focus();
            next.BringIntoView();
        }
        return true;
    }

    private static bool IsInside(DependencyObject ancestor, DependencyObject node)
    {
        for (DependencyObject? cur = node; cur is not null; cur = VisualTreeHelper.GetParent(cur))
            if (cur == ancestor) return true;
        return false;
    }

    // ---------------------------------------------------------------- keyboard / mouse

    private void OnTilesKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is not ListBoxItem { DataContext: LibraryTileViewModel tile } lbi) return;
        switch (e.Key)
        {
            case Key.Left: e.Handled = TryMove(lbi, FocusNavigationDirection.Left); break;
            case Key.Right: e.Handled = TryMove(lbi, FocusNavigationDirection.Right); break;
            case Key.Up: e.Handled = TryMove(lbi, FocusNavigationDirection.Up); break;
            case Key.Down: e.Handled = TryMove(lbi, FocusNavigationDirection.Down); break;
            case Key.Enter:
                Execute(tile.PlayCommand);
                e.Handled = true;
                break;
            case Key.Space:
            case Key.Apps:
                Execute(tile.OpenCommand);
                e.Handled = true;
                break;
        }
    }

    private void OnTilesDoubleClick(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null && d != TileList; d = VisualTreeHelper.GetParent(d))
        {
            if (d is Button) return; // the hover buttons handle their own clicks
            if (d is ListBoxItem { DataContext: LibraryTileViewModel tile })
            {
                Execute(tile.PlayCommand);
                e.Handled = true;
                return;
            }
        }
    }

    private static void Execute(ICommand c)
    {
        if (c.CanExecute(null)) c.Execute(null);
    }

    /// <summary>After a filter switch the focused tile may be gone: focus the selected one once containers exist.</summary>
    public void RefocusAfterFilter() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FocusSelectedTile());
}
