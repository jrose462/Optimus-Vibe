using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameProfiles.App.ViewModels;

namespace GameProfiles.App.Views;

/// <summary>
/// Cover grid. Sizes tiles so whole columns fill the width and one full row fits the height, and does its own
/// row/column focus navigation (WPF's spatial guess skips tiles in ragged rows).
/// </summary>
public partial class LibraryView : UserControl
{
    private const double TargetSlot = 236; // ~5 columns at 1280 DIPs (Ally 7" at 150%)
    private const double CaptionHeight = 64;  // name + store under the art
    private const double SlotGap = 26;

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
        TileList.PreviewKeyDown += OnTilesKeyDown;
        TileList.MouseDoubleClick += OnTilesDoubleClick;
        Pills.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && FocusSelectedTile()) e.Handled = true;
        };
    }

    private LibraryViewModel? Vm => DataContext as LibraryViewModel;

    private void Layout()
    {
        // ScrollViewer padding (14 each side) + a scroll bar's worth so tiles don't reflow when it appears.
        double avail = TileList.ActualWidth - 28 - 14;
        double viewport = TileList.ActualHeight;
        if (avail <= 0 || viewport <= 0) return;

        int cols = Math.Max(2, (int)Math.Floor(avail / TargetSlot));
        double slot = Math.Floor(avail / cols);
        double tile = slot - SlotGap;
        double art = tile * 1.5;                              // Steam portrait covers are 2:3
        double maxArt = viewport - CaptionHeight - 40;        // a whole tile always fits on screen
        if (art > maxArt && maxArt > 150)
        {
            art = maxArt;
            tile = Math.Floor(art / 1.5);
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
        var idx = vm.Filters.ToList().FindIndex(f => f.IsSelected);
        if (Pills.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, idx)) is ContentPresenter cp &&
            VisualTreeHelper.GetChildrenCount(cp) > 0 && VisualTreeHelper.GetChild(cp, 0) is Button b)
            return b.Focus();
        return false;
    }

    public bool IsTile(DependencyObject? d) =>
        d is ListBoxItem lbi && ItemsControl.ItemsControlFromItemContainer(lbi) == TileList;

    /// <summary>Row/column navigation over the wrap grid. Up from the first row goes to the filter pills.</summary>
    public bool TryMove(UIElement focused, FocusNavigationDirection dir)
    {
        if (IsInside(Pills, focused))
            return dir == FocusNavigationDirection.Down && FocusSelectedTile();
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
            if (dir == FocusNavigationDirection.Up) return FocusPills();
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
