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
    private IReadOnlyList<StepRow>? _lastRows;
    private bool _stepsHadFocus;

    public LabGamePanel()
    {
        InitializeComponent();
        // B closes "Not now" (the fix step's session card) or the post-session card only while focus is in it (LabGameViewModel.CancelTransient)
        PostCard.IsKeyboardFocusWithinChanged += (_, _) => UpdateCardFocus();
        StepsHost.IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if (e.NewValue is true) _stepsHadFocus = true;
            UpdateCardFocus();
        };
        // focus that moves to something else in this panel (a card, Manual tools) is not "lost with a removed button"
        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) =>
        {
            if (e.NewFocus is DependencyObject now && !IsInside(now, StepsHost)) _stepsHadFocus = false;
        }));
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnVmChanged;
                _vm.PostSessionFocusRequested -= FocusPostCardSoon;
                _vm.PostCardHasFocus = false;
            }
            _vm = DataContext as LabGameViewModel;
            _lastRows = null;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmChanged;
                _vm.PostSessionFocusRequested += FocusPostCardSoon;
                UpdateCardFocus();
            }
        };
    }

    private void UpdateCardFocus()
    {
        if (_vm is not null) _vm.PostCardHasFocus = PostCard.IsKeyboardFocusWithin || StepsHost.IsKeyboardFocusWithin;
    }

    /// <summary>
    /// "Manual tools" opened from its button: the first action in it takes focus. Closed (B, or the button) while focus was inside: back to the
    /// button. The checklist moved on (the step was done, the list changed) while focus was on its button: the new current step's first button takes it.
    /// </summary>
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.PropertyName == nameof(LabGameViewModel.IsToolsOpen))
        {
            var open = _vm.IsToolsOpen;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (!IsVisible) return;
                if (open)
                {
                    if (ToolsButton.IsKeyboardFocused && FirstAction(ToolsPanel) is { } first) first.Focus();
                }
                else if (Keyboard.FocusedElement is not DependencyObject focused || IsInside(focused, ToolsPanel))
                {
                    if (ToolsButton.IsVisible) ToolsButton.Focus();
                }
            });
            return;
        }
        if (e.PropertyName is nameof(LabGameViewModel.StepRows) or "" or null)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, KeepFocusOnSteps);
    }

    private void KeepFocusOnSteps()
    {
        if (_vm is null || !IsVisible) return;
        var rows = _vm.StepRows;
        if (ReferenceEquals(rows, _lastRows)) return;
        _lastRows = rows;
        if (!_stepsHadFocus || StepsHost.IsKeyboardFocusWithin) return;
        // the button that had focus is gone; focus that went somewhere else on purpose stays there
        if (Keyboard.FocusedElement is DependencyObject focused && !ReferenceEquals(focused, Window.GetWindow(this)) && IsVisibleElement(focused)) return;
        if (FirstAction(StepsHost) is { } next) next.Focus();
    }

    private static bool IsVisibleElement(DependencyObject d) => d is not UIElement { IsVisible: false };

    /// <summary>
    /// The card asks for focus (it just arrived, or the window just came back): the checklist's current step (or the card that stayed a card)
    /// takes it when this panel is on screen in the active window, i.e. the player came back from the game to this game's page. Otherwise
    /// nothing moves.
    /// </summary>
    private void FocusPostCardSoon()
    {
        if (_vm is not { PostSessionWantsFocus: true }) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_vm is not { PostSessionWantsFocus: true } vm || !IsVisible || Window.GetWindow(this) is not { IsActive: true }) return;
            var target = PostCard.IsVisible ? FirstAction(PostCard) : FirstAction(StepsHost);
            if (target is null) return;
            target.Focus();
            vm.PostSessionFocusTaken();
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
