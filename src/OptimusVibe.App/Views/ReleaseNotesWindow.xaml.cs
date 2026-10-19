using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OptimusVibe.App.Input;
using OptimusVibe.App.Services;
using OptimusVibe.Logging;

namespace OptimusVibe.App.Views;

/// <summary>
/// Release notes in their own window (Settings > "A new version is ready" > Pop out), rendered from Markdown at a
/// reading size. One at a time: opening again replaces the content and brings it forward. Esc / B close it; D-pad
/// up/down, the right stick, Page Up/Down scroll (the main window's gamepad navigator only drives the main window).
/// </summary>
public partial class ReleaseNotesWindow : Window
{
    private static ReleaseNotesWindow? _open;

    private readonly DispatcherTimer _pad = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly bool[] _connected = new bool[XInput.MaxControllers];
    private DateTime _nextProbe = DateTime.MinValue;
    private GamepadButtons _previous;
    private bool _armed;

    private ReleaseNotesWindow()
    {
        InitializeComponent();
        Density.Attach(this);
        PreviewKeyDown += OnKey;
        StateChanged += (_, _) => UpdateMaxButton();
        Activated += (_, _) => { _armed = false; if (XInput.IsAvailable) _pad.Start(); };
        Deactivated += (_, _) => _pad.Stop();
        Closed += (_, _) => { _pad.Stop(); if (_open == this) _open = null; };
        _pad.Tick += (_, _) => Poll();
    }

    /// <summary>Show <paramref name="markdown"/> titled <paramref name="title"/>, over <paramref name="owner"/>.</summary>
    public static ReleaseNotesWindow Show(string title, string? markdown, Window? owner)
    {
        var w = _open ?? new ReleaseNotesWindow();
        w.Title = title;
        w.Viewer.Document = MarkdownFlow.ToDocument(markdown, fontSize: 16);
        if (_open is null)
        {
            _open = w;
            if (owner is { IsVisible: true })
            {
                w.Owner = owner;
                // a small screen (the Ally) or a maximised app: fill it; otherwise a comfortable reading size
                if (owner.WindowState == WindowState.Maximized || owner.ActualWidth < 1000) w.WindowState = WindowState.Maximized;
                else
                {
                    w.Width = Math.Min(900, owner.ActualWidth * 0.9);
                    w.Height = Math.Max(w.MinHeight, owner.ActualHeight * 0.85);
                }
            }
            w.Show();
        }
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        w.Viewer.Focus();
        w.UpdateMaxButton();
        return w;
    }

    private void UpdateMaxButton()
    {
        var max = WindowState == WindowState.Maximized;
        MaxRestoreButton.Content = max ? "" : "";
        MaxRestoreButton.ToolTip = max ? "Restore" : "Maximize";
        System.Windows.Automation.AutomationProperties.SetName(MaxRestoreButton, max ? "Restore" : "Maximize");
    }

    private void OnMaximize(object sender, ExecutedRoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, ExecutedRoutedEventArgs e) => Close();
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- gamepad

    private ScrollViewer? Scroller => FindScroller(Viewer);

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScroller(child) is { } found) return found;
        }
        return null;
    }

    private void Poll()
    {
        try
        {
            var now = DateTime.UtcNow;
            var probe = now >= _nextProbe;
            if (probe) _nextProbe = now + TimeSpan.FromSeconds(2);
            var buttons = GamepadButtons.None;
            short ry = 0;
            for (var i = 0; i < XInput.MaxControllers; i++)
            {
                if (!_connected[i] && !probe) continue;
                _connected[i] = XInput.TryGetState(i, out var s);
                if (!_connected[i]) continue;
                buttons |= s.Gamepad.Buttons;
                if (Math.Abs((int)s.Gamepad.ThumbRY) > Math.Abs((int)ry)) ry = s.Gamepad.ThumbRY;
            }
            // the press that opened this window (A in Settings) must be released first
            if (!_armed)
            {
                if (buttons == GamepadButtons.None) _armed = true;
                _previous = buttons;
                return;
            }
            var pressed = buttons & ~_previous;
            _previous = buttons;

            if (pressed.HasFlag(GamepadButtons.B)) { Close(); return; }
            if (Scroller is not { } sv) return;
            if (buttons.HasFlag(GamepadButtons.DPadDown)) sv.ScrollToVerticalOffset(sv.VerticalOffset + 14);
            else if (buttons.HasFlag(GamepadButtons.DPadUp)) sv.ScrollToVerticalOffset(sv.VerticalOffset - 14);
            else if (Math.Abs((int)ry) > 8000) sv.ScrollToVerticalOffset(sv.VerticalOffset - ry / 1500.0);
        }
        catch (Exception ex)
        {
            Log.Warn($"Release notes gamepad: {ex.Message}");
        }
    }
}
