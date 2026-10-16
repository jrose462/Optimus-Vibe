using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameProfiles.App.Input;
using GameProfiles.App.ViewModels;

namespace GameProfiles.App.Views;

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

        FocusCue.Attach(this);
        _focusKeeper = new FocusKeeper(this, FocusDefault);
        _ = new GamepadNavigator(this, this);

        PreviewKeyDown += OnPreviewKeyDown;
        vm.PropertyChanged += OnViewModelChanged;
        SourceInitialized += (_, _) => UseDarkTitleBar();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void UseDarkTitleBar()
    {
        const int DwmwaUseImmersiveDarkMode = 20; // Windows 10 20H1+ / 11
        int on = 1;
        try { DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DwmwaUseImmersiveDarkMode, ref on, sizeof(int)); }
        catch (Exception) { /* older Windows: keep the default caption */ }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsBusy) when !_vm.IsBusy:
                _focusKeeper.RestoreSoon();
                break;
            case nameof(MainViewModel.State):
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
            // Ctrl+Tab from anywhere switches tabs (the TabControl only handles it when focus is inside).
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) PreviousTab(); else NextTab();
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- IGamepadActions

    public void NextTab() => SwitchTab(+1);
    public void PreviousTab() => SwitchTab(-1);

    private void SwitchTab(int delta)
    {
        if (_vm.SelectedDetail is not { HasGame: true } detail || detail.IsPreviewOpen) return;
        bool wasInDetail = IsInDetail();
        detail.SelectedTabIndex = (detail.SelectedTabIndex + delta + 2) % 2;
        if (wasInDetail) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Detail.FocusSelectedTab);
    }

    public void Sync() => Execute(_vm.SyncCommand);
    public void Launch() => Execute(_vm.LaunchCommand);
    public void Refresh() => Execute(_vm.RefreshCommand);

    public bool Back() => _vm.CancelTransient();

    public void FocusGameList()
    {
        if (!_vm.IsReady) return;
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
        if (_vm.IsReady && GameList.Items.Count > 0)
        {
            FocusGameList();
            if (GameList.IsKeyboardFocusWithin) return;
        }
        MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    public void OnFocusMoved(IInputElement? focused)
    {
        // Selection follows the gamepad in the game list, like arrow keys do natively.
        if (focused is ListBoxItem { DataContext: GameItemViewModel } lbi && ItemsControl.ItemsControlFromItemContainer(lbi) == GameList)
            lbi.IsSelected = true;
    }

    // ---------------------------------------------------------------- helpers

    private bool IsInDetail() =>
        Keyboard.FocusedElement is DependencyObject d && IsDescendant(Detail, d);

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
