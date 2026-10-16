using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace OptimusVibe.App.Input;

/// <summary>
/// Gamepad users can't click back into the window, so focus must never just disappear. When the focused element
/// is disabled (busy), collapsed (closed panel) or removed (reloaded list), focus returns to the most recent
/// still-usable element once the UI settles; <paramref name="fallback"/> is used when none is left.
/// </summary>
public sealed class FocusKeeper
{
    private const int Depth = 12;
    private readonly Window _window;
    private readonly Action _fallback;
    private readonly LinkedList<WeakReference<UIElement>> _history = new();
    private bool _pending;

    public FocusKeeper(Window window, Action fallback)
    {
        _window = window;
        _fallback = fallback;
        window.PreviewGotKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus is UIElement el && el != window) Remember(el);
        };
        window.PreviewLostKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus is null || e.NewFocus == window) RestoreSoon();
        };
    }

    private void Remember(UIElement el)
    {
        for (var n = _history.First; n is not null; n = n.Next)
            if (n.Value.TryGetTarget(out var t) && t == el) { _history.Remove(n); break; }
        _history.AddFirst(new WeakReference<UIElement>(el));
        while (_history.Count > Depth) _history.RemoveLast();
    }

    /// <summary>Restore after pending layout and command re-query (buttons re-enable at Background priority).</summary>
    public void RestoreSoon()
    {
        if (_pending) return;
        _pending = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            _pending = false;
            if (!_window.IsActive) return;
            if (Keyboard.FocusedElement is UIElement cur && cur != _window && cur.IsVisible && Window.GetWindow(cur) == _window) return;
            foreach (var w in _history)
            {
                if (w.TryGetTarget(out var el) && el.IsVisible && el.IsEnabled && el.Focusable &&
                    Window.GetWindow(el) == _window && el.Focus())
                    return;
            }
            _fallback();
        });
    }
}
