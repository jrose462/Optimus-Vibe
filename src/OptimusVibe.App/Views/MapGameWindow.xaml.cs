using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using OptimusVibe.App.Input;
using OptimusVibe.App.Services;
using OptimusVibe.App.ViewModels;
using OptimusVibe.Logging;

namespace OptimusVibe.App.Views;

/// <summary>
/// "Map this game" as a full-screen, topmost window (the same shell as the post-game prompt): A does the main action, Y keeps the changes
/// as a manual save, B goes back / closes; mouse, touch and keyboard (Esc) work too. It hides while the game runs and takes the
/// foreground again when the game has exited.
/// </summary>
public partial class MapGameWindow : Window
{
    private readonly MapGameModel _model;
    private readonly DispatcherTimer _pad = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _grab = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly bool[] _connected = new bool[XInput.MaxControllers];
    private DateTime _nextProbe = DateTime.MinValue;
    private GamepadButtons _previous;
    private bool _armed;
    private int _grabTries;

    public MapGameWindow(MapGameModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        FocusCue.Attach(this);
        Density.Attach(this);
        FocusCue.SetShow(this, true);

        SourceInitialized += (_, _) => ForegroundWindow.Grab(new WindowInteropHelper(this).Handle);
        ContentRendered += (_, _) =>
        {
            Activate();
            PrimaryButton.Focus();
            Keyboard.Focus(PrimaryButton);
            _grab.Start();
        };
        _grab.Tick += (_, _) =>
        {
            if (IsActive || ++_grabTries > 8) { _grab.Stop(); return; }
            ForegroundWindow.Grab(new WindowInteropHelper(this).Handle);
        };
        _pad.Tick += (_, _) => Poll();
        PreviewKeyDown += OnKey;
        PreviewMouseDown += (_, _) => FocusCue.SetShow(this, false);
        Loaded += (_, _) => { if (XInput.IsAvailable) _pad.Start(); };
        Closed += (_, _) => { _pad.Stop(); _grab.Stop(); };

        model.HideRequested += () => Dispatcher.Invoke(Hide);
        model.ShowRequested += () => Dispatcher.Invoke(Reappear);
        model.CloseRequested += () => Dispatcher.Invoke(Close);
        model.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusFirstButton);
    }

    private void Reappear()
    {
        _armed = false; // the game's last button press may still be held: wait until it is released
        Show();
        _grabTries = 0;
        ForegroundWindow.Grab(new WindowInteropHelper(this).Handle);
        _grab.Start();
        FocusFirstButton();
    }

    private void FocusFirstButton()
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.Button f && f.IsVisible) return;
        foreach (var b in new[] { PrimaryButton, SecondaryButton, BackButton })
            if (b.IsVisible && b.IsEnabled)
            {
                b.Focus();
                Keyboard.Focus(b);
                return;
            }
    }

    private void OnPrimary(object sender, RoutedEventArgs e) => _model.Primary();
    private void OnSecondary(object sender, RoutedEventArgs e) => _model.Secondary();
    private void OnBack(object sender, RoutedEventArgs e) => _model.Back();

    private void OnKey(object sender, KeyEventArgs e)
    {
        FocusCue.SetShow(this, true);
        if (e.Key == Key.Escape)
        {
            _model.Back();
            e.Handled = true;
        }
    }

    private void Poll()
    {
        try
        {
            if (!IsVisible) return;
            var now = DateTime.UtcNow;
            var probe = now >= _nextProbe;
            if (probe) _nextProbe = now + TimeSpan.FromSeconds(2);
            var buttons = GamepadButtons.None;
            for (var i = 0; i < XInput.MaxControllers; i++)
            {
                if (!_connected[i] && !probe) continue;
                _connected[i] = XInput.TryGetState(i, out var s);
                if (_connected[i]) buttons |= s.Gamepad.Buttons;
            }
            if (!_armed)
            {
                if (buttons == GamepadButtons.None) _armed = true;
                _previous = buttons;
                return;
            }
            var pressed = buttons & ~_previous;
            _previous = buttons;
            if (pressed == GamepadButtons.None) return;
            FocusCue.SetShow(this, true);
            if (pressed.HasFlag(GamepadButtons.A)) _model.Primary();
            else if (pressed.HasFlag(GamepadButtons.Y)) _model.Secondary();
            else if (pressed.HasFlag(GamepadButtons.B)) _model.Back();
        }
        catch (Exception ex)
        {
            Log.Warn($"Map this game gamepad: {ex.Message}");
        }
    }
}
