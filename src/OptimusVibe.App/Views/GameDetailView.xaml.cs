using OptimusVibe.App.ViewModels;
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
        WireIntentOverlay();
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

    // ---------------------------------------------------------------- intent card: expands over the top third of the window

    /// <summary>Put focus on the intent card (gamepad / keyboard focus expands it).</summary>
    public void ScrollToTop() => Main.ScrollToTop();

    public bool FocusIntentCard() => IntentButton.IsVisible && IntentButton.Focus();

    private GameDetailViewModel? _intentModel;
    private Window? _intentWindow;

    private void WireIntentOverlay()
    {
        DataContextChanged += (_, _) => AttachIntentModel();
        IntentButton.IsKeyboardFocusedChanged += (_, _) => UpdateIntentOverlay();
        SizeChanged += (_, _) => UpdateIntentOverlay();
        Loaded += (_, _) =>
        {
            AttachIntentModel();
            _intentWindow = Window.GetWindow(this);
            if (_intentWindow is not null) _intentWindow.SizeChanged += OnIntentWindowSized;
            UpdateIntentOverlay();
        };
        Unloaded += (_, _) =>
        {
            if (_intentWindow is not null) _intentWindow.SizeChanged -= OnIntentWindowSized;
            _intentWindow = null;
        };
    }

    private void OnIntentWindowSized(object? sender, SizeChangedEventArgs e) => UpdateIntentOverlay();

    private void AttachIntentModel()
    {
        if (ReferenceEquals(_intentModel, DataContext)) return;
        if (_intentModel is not null) _intentModel.PropertyChanged -= OnIntentModelChanged;
        _intentModel = DataContext as GameDetailViewModel;
        if (_intentModel is not null) _intentModel.PropertyChanged += OnIntentModelChanged;
        UpdateIntentOverlay();
    }

    // Debug scenes (Debug builds only) take screenshots without the on-screen keyboard in the way.
    private static bool InDebugScene =>
#if DEBUG
        Services.DebugScenes.Scene is not null;
#else
        false;
#endif

    private void OnIntentModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GameDetailViewModel.IsEditingIntent))
        {
            var editing = _intentModel?.IsEditingIntent == true;
            UpdateIntentOverlay();
            if (editing)
            {
                // controller in hand: bring up the on-screen keyboard right away, like the capture prompt does
                if (_intentWindow is { } w && Input.PadState.GetIsConnected(w) && !InDebugScene)
                    Dispatcher.BeginInvoke(new Action(Input.TouchKeyboard.Show), System.Windows.Threading.DispatcherPriority.Input);
            }
            else
            {
                // the editor closed: focus goes back to the card (it stays open while it has focus; the next B / move collapses it)
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var f = Keyboard.FocusedElement as DependencyObject;
                    if (IntentCard.IsVisible && (f is null || f == this || !IsAncestorOf(f) || IntentOverlay.IsAncestorOf(f)))
                        IntentButton.Focus();
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
        else if (e.PropertyName is nameof(GameDetailViewModel.HasIntent)) UpdateIntentOverlay();
    }

    /// <summary>Show the overlay while the card has keyboard / gamepad focus or is being edited; size it to a third of the window.</summary>
    private void UpdateIntentOverlay()
    {
        var on = _intentModel is { HasIntent: true } im && (im.IsEditingIntent || IntentButton.IsKeyboardFocused);
        if (on)
        {
            var window = _intentWindow ?? Window.GetWindow(this);
            var m = (Thickness)FindResource("DetailMargin");
            IntentOverlay.Height = Math.Max(150, window is null ? ActualHeight * 0.4 : window.ActualHeight / 3);   // one third of the window
            IntentOverlay.Margin = new Thickness(m.Left, m.Top, m.Right, 0);
        }
        IntentOverlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnIntentOverlayClick(object sender, MouseButtonEventArgs e)
    {
        if (_intentModel is { IsEditingIntent: false } m && e.OriginalSource is not System.Windows.Controls.Primitives.ScrollBar && m.EditIntentCommand.CanExecute(null))
            m.EditIntentCommand.Execute(null);
    }
}
