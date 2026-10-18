using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using OptimusVibe.App.Input;
using OptimusVibe.App.Services;
using OptimusVibe.App.ViewModels;

namespace OptimusVibe.App.Views;

public partial class MainWindow : Window, IGamepadActions
{
    private readonly MainViewModel _vm;
    private readonly FocusKeeper _focusKeeper;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        // The Ally's 7" panel at 125-150% is ~1280-1536 DIPs wide: use the whole screen there.
        if (SystemParameters.PrimaryScreenWidth < 1600 || SystemParameters.PrimaryScreenHeight < 900)
            WindowState = WindowState.Maximized;
#if DEBUG
        DebugScenes.ApplyWindowOverride(this);
#endif

        FocusCue.Attach(this);
        _focusKeeper = new FocusKeeper(this, FocusDefault);
        var pad = new GamepadNavigator(this, this);
        pad.ControllerChanged += connected =>
        {
            _vm.IsGamepadConnected = connected;
            PadState.SetIsConnected(this, connected);
        };
        Density.Attach(this);

        PreviewKeyDown += OnPreviewKeyDown;
        vm.PropertyChanged += OnViewModelChanged;
        vm.Library.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryViewModel.Filter) && _vm.IsLibraryPage) LibraryPage.RefocusAfterFilter();
        };
        vm.MinimizeRequested += () => WindowState = WindowState.Minimized;
        vm.RestoreRequested += RestoreFromGame;
        SourceInitialized += (_, _) => UseDarkTitleBar();
        Activated += (_, _) => _vm.OnWindowActivated();

        // Title bar: the header row is the caption; keep CaptionHeight equal to it, and compensate the maximized overflow.
        HeaderBar.SizeChanged += (_, e) => WindowChrome.GetWindowChrome(this).CaptionHeight = e.NewSize.Height;
        StateChanged += (_, _) => UpdateWindowState();
        UpdateWindowState();
    }

    private void UpdateWindowState()
    {
        var maximized = WindowState == WindowState.Maximized;
        // A WindowChrome window maximizes with its resize border hanging off the screen edges: pull the content back in.
        RootGrid.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaxRestoreButton.Content = maximized ? "E923" : "E922"; // Fluent: restore / maximize
        MaxRestoreButton.Command = maximized ? SystemCommands.RestoreWindowCommand : SystemCommands.MaximizeWindowCommand;
        MaxRestoreButton.ToolTip = maximized ? "Restore" : "Maximize";
        System.Windows.Automation.AutomationProperties.SetName(MaxRestoreButton, maximized ? "Restore" : "Maximize");
    }

    private void OnMinimize(object sender, ExecutedRoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void OnMaximize(object sender, ExecutedRoutedEventArgs e) => SystemCommands.MaximizeWindow(this);
    private void OnRestore(object sender, ExecutedRoutedEventArgs e) => SystemCommands.RestoreWindow(this);
    private void OnClose(object sender, ExecutedRoutedEventArgs e) => SystemCommands.CloseWindow(this);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void UseDarkTitleBar()
    {
        const int DwmwaUseImmersiveDarkMode = 20; // Windows 10 20H1+ / 11
        int on = 1;
        try { DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DwmwaUseImmersiveDarkMode, ref on, sizeof(int)); }
        catch (Exception) { /* older Windows: keep the default caption */ }
    }

    private void RestoreFromGame()
    {
        if (WindowState != WindowState.Minimized) return;
        WindowState = SystemParameters.PrimaryScreenWidth < 1600 || SystemParameters.PrimaryScreenHeight < 900
            ? WindowState.Maximized
            : WindowState.Normal;
        Activate();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FocusDefault);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsBusy) when !_vm.IsBusy:
                _focusKeeper.RestoreSoon();
                break;
            case nameof(MainViewModel.State):
            case nameof(MainViewModel.Page):
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FocusDefault);
                break;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = Back();
        }
        else if (e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !IsInDetail())
        {
            // Ctrl+Tab from anywhere switches tabs / filters (the TabControl only handles it when focus is inside).
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) PreviousTab(); else NextTab();
            e.Handled = true;
        }
        else if (e.Key == Key.Back && _vm.IsDetailPage && Keyboard.FocusedElement is not TextBox)
        {
            _vm.CloseDetail();
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- IGamepadActions

    public void NextTab() => SwitchTab(+1);
    public void PreviousTab() => SwitchTab(-1);

    private void SwitchTab(int delta)
    {
        if (_vm.IsLibraryPage)
        {
            if (delta > 0) _vm.Library.NextFilter(); else _vm.Library.PreviousFilter();
            return;
        }
        if (_vm.IsSettingsPage)
        {
            SettingsPage.JumpSection(delta);
            return;
        }
        if (!_vm.IsManagePage && !_vm.IsDetailPage) return;
        if (_vm.IsDetailPage && !_vm.DetailHasConfig) return;
        if (_vm.SelectedDetail is not { HasGame: true } detail || detail.IsPreviewOpen) return;
        bool wasInDetail = IsInDetail();
        detail.SelectedTabIndex = (detail.SelectedTabIndex + delta + 2) % 2;
        if (wasInDetail || _vm.IsDetailPage) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ActiveGameDetail.FocusSelectedTab());
    }

    public void Sync() => Execute(_vm.SyncCommand);

    public void Menu()
    {
        if (_vm.IsSettingsPage) _vm.CloseSettings();
        else Execute(_vm.OpenSettingsCommand);
    }

    /// <summary>X: library → open the focused game's presets; manage and a game page → launch it.</summary>
    public void Secondary()
    {
        if (_vm.IsLibraryPage)
        {
            var tile = (Keyboard.FocusedElement as ListBoxItem)?.DataContext as LibraryTileViewModel ?? _vm.Library.SelectedTile;
            if (tile is not null) Execute(tile.OpenCommand);
        }
        else if (_vm.IsManagePage)
        {
            Execute(_vm.LaunchCommand);
        }
        else if (_vm.IsDetailPage && _vm.DetailTile is { } playing)
        {
            Execute(playing.PlayCommand);
        }
    }

    public void Refresh() => Execute(_vm.RefreshCommand);

    public bool Back() => _vm.CancelTransient();

    /// <summary>B with nothing to close: manage → the game list; library → the selected tile.</summary>
    public void FocusGameList()
    {
        if (!_vm.IsReady) return;
        if (_vm.IsLibraryPage)
        {
            LibraryPage.FocusSelectedTile();
            return;
        }
        if (!_vm.IsManagePage) return;
        if (GameList.SelectedItem is { } item && GameList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem lbi)
        {
            lbi.Focus();
            lbi.BringIntoView();
        }
        else if (GameList.Items.Count > 0 && GameList.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem first)
        {
            first.Focus();
        }
    }

    public void FocusDefault()
    {
        if (_vm.IsReady)
        {
            switch (_vm.Page)
            {
                case AppPage.Library:
                    if (LibraryPage.FocusSelectedTile() || LibraryPage.FocusPills()) return;
                    break;
                case AppPage.Detail:
                    LibraryDetail.FocusDefault(_vm.DetailHasConfig);
                    if (LibraryDetail.IsKeyboardFocusWithin) return;
                    break;
                case AppPage.Settings:
                    SettingsPage.FocusDefault();
                    if (SettingsPage.IsKeyboardFocusWithin) return;
                    break;
                default:
                    if (GameList.Items.Count > 0)
                    {
                        FocusGameList();
                        if (GameList.IsKeyboardFocusWithin) return;
                    }
                    break;
            }
        }
        MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    public void OnFocusMoved(IInputElement? focused)
    {
        // Selection follows the gamepad in the game list, like arrow keys do natively.
        if (focused is ListBoxItem { DataContext: GameItemViewModel } lbi && ItemsControl.ItemsControlFromItemContainer(lbi) == GameList)
            lbi.IsSelected = true;
    }

    public bool TryMove(UIElement focused, FocusNavigationDirection direction) =>
        _vm.IsLibraryPage && LibraryPage.TryMove(focused, direction);

    /// <summary>A on a library tile plays it.</summary>
    public bool TryActivate(UIElement focused)
    {
        if (_vm.IsLibraryPage && LibraryPage.IsTile(focused) && focused is ListBoxItem { DataContext: LibraryTileViewModel tile })
        {
            Execute(tile.PlayCommand);
            return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- helpers

    private GameDetailView ActiveGameDetail => _vm.IsDetailPage ? LibraryDetail.GameDetail : Detail;

    private bool IsInDetail() =>
        Keyboard.FocusedElement is DependencyObject d && IsDescendant(ActiveGameDetail, d);

    private static bool IsDescendant(DependencyObject ancestor, DependencyObject node)
    {
        for (DependencyObject? cur = node; cur is not null; cur = cur is Visual ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur == ancestor) return true;
        return false;
    }

    private static void Execute(ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }
}
