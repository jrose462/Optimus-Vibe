using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace GameProfiles.App.Input;

/// <summary>
/// FocusCue.Show (inherited): true while the user drives the app with keyboard or gamepad, false after mouse/touch.
/// Styles show the thick focus ring only when IsKeyboardFocused AND FocusCue.Show, so clicking doesn't leave rings behind.
/// </summary>
public static class FocusCue
{
    public static readonly DependencyProperty ShowProperty = DependencyProperty.RegisterAttached(
        "Show", typeof(bool), typeof(FocusCue),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetShow(DependencyObject d) => (bool)d.GetValue(ShowProperty);
    public static void SetShow(DependencyObject d, bool value) => d.SetValue(ShowProperty, value);

    /// <summary>Hook a window: keys turn the cue on, mouse/touch/stylus turn it off.</summary>
    public static void Attach(Window window)
    {
        window.PreviewKeyDown += (_, _) => SetShow(window, true);
        window.PreviewMouseDown += (_, _) => SetShow(window, false);
        window.PreviewTouchDown += (_, _) => SetShow(window, false);
        window.PreviewStylusDown += (_, _) => SetShow(window, false);
    }

    // ---------------------------------------------------------------- FocusWhenShown

    /// <summary>Focus the element whenever it becomes visible (inline confirm panels, rename box, diff panel).</summary>
    public static readonly DependencyProperty FocusWhenShownProperty = DependencyProperty.RegisterAttached(
        "FocusWhenShown", typeof(bool), typeof(FocusCue), new PropertyMetadata(false, OnFocusWhenShownChanged));

    public static bool GetFocusWhenShown(DependencyObject d) => (bool)d.GetValue(FocusWhenShownProperty);
    public static void SetFocusWhenShown(DependencyObject d, bool value) => d.SetValue(FocusWhenShownProperty, value);

    private static void OnFocusWhenShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        el.IsVisibleChanged -= OnVisibleChanged;
        if (e.NewValue is true)
        {
            el.IsVisibleChanged += OnVisibleChanged;
            if (el.IsVisible) FocusSoon(el);
        }
    }

    private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is UIElement el && e.NewValue is true) FocusSoon(el);
    }

    public static void FocusSoon(UIElement el) =>
        el.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!el.IsVisible) return;
            el.Focus();
            Keyboard.Focus(el);
            if (el is TextBox tb) tb.SelectAll();
        });
}
