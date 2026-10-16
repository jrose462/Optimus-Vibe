using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameProfiles.Logging;

namespace GameProfiles.App.Input;

/// <summary>What the window does for the non-navigation buttons.</summary>
public interface IGamepadActions
{
    void NextTab();
    void PreviousTab();
    void Sync();
    void Launch();
    void Refresh();

    /// <summary>B: close a confirm panel / form; false when nothing was open.</summary>
    bool Back();

    /// <summary>Put focus somewhere sensible (the selected game) when nothing in the window has it.</summary>
    void FocusDefault();

    /// <summary>B with nothing to close: jump back to the game list.</summary>
    void FocusGameList();

    /// <summary>Called after a gamepad focus move (e.g. so the game list selection follows focus).</summary>
    void OnFocusMoved(IInputElement? focused);
}

/// <summary>
/// Polls XInput at ~60 Hz while the window is active and turns it into focus navigation:
/// D-pad / left stick move focus (repeat after 400 ms, then every 120 ms), A activates, B goes back,
/// LB/RB switch tabs, Y syncs, Start launches, View refreshes, right stick scrolls.
/// </summary>
public sealed class GamepadNavigator : IDisposable
{
    private const int StickThreshold = 16000;     // ~50% deflection for navigation
    private const int ScrollDeadZone = 7849;      // XINPUT_GAMEPAD_RIGHT_THUMB_DEADZONE
    private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(2);

    private readonly Window _window;
    private readonly IGamepadActions _actions;
    private readonly DispatcherTimer _timer;
    private readonly bool[] _connected = new bool[XInput.MaxControllers];
    private DateTime _nextProbe = DateTime.MinValue;
    private GamepadButtons _previous;
    private FocusNavigationDirection? _heldDirection;
    private DateTime _nextRepeat;
    private bool _swallowUntilReleased = true;

    public GamepadNavigator(Window window, IGamepadActions actions)
    {
        _window = window;
        _actions = actions;
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Poll();
        window.Activated += (_, _) => Start();
        window.Deactivated += (_, _) => Stop();
        window.Closed += (_, _) => Dispose();
        if (window.IsActive) Start();
    }

    /// <summary>True once any controller has been seen.</summary>
    public bool HasController { get; private set; }

    private void Start()
    {
        if (!XInput.IsAvailable) return;
        // Buttons still held from the moment the window was activated (e.g. A on a launcher) are ignored.
        _swallowUntilReleased = true;
        _nextProbe = DateTime.MinValue;
        _timer.Start();
    }

    private void Stop()
    {
        _timer.Stop();
        _heldDirection = null;
        _previous = GamepadButtons.None;
    }

    public void Dispose() => _timer.Stop();

    private void Poll()
    {
        try
        {
            if (!ReadMerged(out var buttons, out var lx, out var ly, out var ry))
            {
                _previous = GamepadButtons.None;
                _heldDirection = null;
                if (!XInput.IsAvailable) _timer.Stop();
                return;
            }

            if (_swallowUntilReleased)
            {
                if (buttons == GamepadButtons.None && Direction(buttons, lx, ly) is null) _swallowUntilReleased = false;
                _previous = buttons;
                return;
            }

            var pressed = buttons & ~_previous;
            _previous = buttons;
            if (pressed != GamepadButtons.None) FocusCue.SetShow(_window, true);

            if (pressed.HasFlag(GamepadButtons.A)) Activate();
            if (pressed.HasFlag(GamepadButtons.B) && !_actions.Back()) _actions.FocusGameList();
            if (pressed.HasFlag(GamepadButtons.LeftShoulder)) _actions.PreviousTab();
            if (pressed.HasFlag(GamepadButtons.RightShoulder)) _actions.NextTab();
            if (pressed.HasFlag(GamepadButtons.Y)) _actions.Sync();
            if (pressed.HasFlag(GamepadButtons.Start)) _actions.Launch();
            if (pressed.HasFlag(GamepadButtons.Back)) _actions.Refresh();

            HandleDirection(Direction(buttons, lx, ly));
            if (Math.Abs((int)ry) > ScrollDeadZone) Scroll(ry);
        }
        catch (Exception ex)
        {
            Log.Error("Gamepad input failed", ex);
        }
    }

    /// <summary>OR of all connected pads; sticks from whichever is deflected most. Disconnected slots are re-probed every 2 s (XInput is slow on empty slots).</summary>
    private bool ReadMerged(out GamepadButtons buttons, out short lx, out short ly, out short ry)
    {
        buttons = GamepadButtons.None;
        lx = ly = ry = 0;
        bool probe = DateTime.UtcNow >= _nextProbe;
        if (probe) _nextProbe = DateTime.UtcNow + ProbeInterval;
        bool any = false;
        for (int i = 0; i < XInput.MaxControllers; i++)
        {
            if (!_connected[i] && !probe) continue;
            _connected[i] = XInput.TryGetState(i, out var s);
            if (!_connected[i]) continue;
            any = true;
            var g = s.Gamepad;
            buttons |= g.Buttons;
            if (Math.Abs((int)g.ThumbLX) + Math.Abs((int)g.ThumbLY) > Math.Abs((int)lx) + Math.Abs((int)ly))
            {
                lx = g.ThumbLX;
                ly = g.ThumbLY;
            }
            if (Math.Abs((int)g.ThumbRY) > Math.Abs((int)ry)) ry = g.ThumbRY;
        }
        if (any && !HasController)
        {
            HasController = true;
            Log.Info("Gamepad connected");
        }
        return any;
    }

    private static FocusNavigationDirection? Direction(GamepadButtons b, short lx, short ly)
    {
        if (b.HasFlag(GamepadButtons.DPadUp)) return FocusNavigationDirection.Up;
        if (b.HasFlag(GamepadButtons.DPadDown)) return FocusNavigationDirection.Down;
        if (b.HasFlag(GamepadButtons.DPadLeft)) return FocusNavigationDirection.Left;
        if (b.HasFlag(GamepadButtons.DPadRight)) return FocusNavigationDirection.Right;
        int x = lx, y = ly;
        if (Math.Max(Math.Abs(x), Math.Abs(y)) < StickThreshold) return null;
        if (Math.Abs(x) > Math.Abs(y)) return x > 0 ? FocusNavigationDirection.Right : FocusNavigationDirection.Left;
        return y > 0 ? FocusNavigationDirection.Up : FocusNavigationDirection.Down; // XInput Y is up-positive
    }

    private void HandleDirection(FocusNavigationDirection? dir)
    {
        var now = DateTime.UtcNow;
        if (dir is null)
        {
            _heldDirection = null;
            return;
        }
        if (dir != _heldDirection)
        {
            _heldDirection = dir;
            _nextRepeat = now + RepeatDelay;
            FocusCue.SetShow(_window, true);
            Move(dir.Value);
        }
        else if (now >= _nextRepeat)
        {
            _nextRepeat = now + RepeatInterval;
            Move(dir.Value);
        }
    }

    private UIElement? FocusedInWindow() =>
        Keyboard.FocusedElement is UIElement el && el != _window && el.IsVisible && Window.GetWindow(el) == _window ? el : null;

    private void Move(FocusNavigationDirection dir)
    {
        var el = FocusedInWindow();
        if (el is null)
        {
            _actions.FocusDefault();
            return;
        }
        if (el.MoveFocus(new TraversalRequest(dir)))
        {
            var focused = Keyboard.FocusedElement;
            (focused as FrameworkElement)?.BringIntoView();
            _actions.OnFocusMoved(focused);
        }
    }

    /// <summary>A: click buttons, toggle checkboxes, select tabs and list items via their automation patterns.</summary>
    private void Activate()
    {
        var el = FocusedInWindow();
        if (el is null)
        {
            _actions.FocusDefault();
            return;
        }
        if (!el.IsEnabled) return;
        switch (el)
        {
            case ListBoxItem item:
                item.IsSelected = true;
                return;
            case TabItem tab:
                tab.IsSelected = true;
                return;
            case TextBox:
                return; // typing needs the touch keyboard; defaults are prefilled so A is not required here
        }
        var peer = UIElementAutomationPeer.CreatePeerForElement(el);
        switch (peer?.GetPattern(PatternInterface.Invoke))
        {
            case IInvokeProvider invoke:
                invoke.Invoke();
                return;
        }
        if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle) toggle.Toggle();
        else if (peer?.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider sel) sel.Select();
        else if (el is ButtonBase b) b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, b));
    }

    /// <summary>Right stick: scroll the scroll viewer around the focused element.</summary>
    private void Scroll(short ry)
    {
        DependencyObject? d = FocusedInWindow();
        while (d is not null and not ScrollViewer) d = VisualTreeHelper.GetParent(d);
        if (d is ScrollViewer sv) sv.ScrollToVerticalOffset(sv.VerticalOffset - ry / 32767.0 * 24);
    }
}
