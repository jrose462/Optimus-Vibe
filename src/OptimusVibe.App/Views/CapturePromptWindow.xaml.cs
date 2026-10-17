using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using OptimusVibe.App.Input;
using OptimusVibe.App.Services;
using OptimusVibe.App.ViewModels;
using OptimusVibe.Launch;
using OptimusVibe.Logging;

namespace OptimusVibe.App.Views;

/// <summary>What the window shows; a plain view model so the window stays thin.</summary>
public sealed class CapturePromptModel : ObservableObject
{
    private bool _isNaming;
    private string _saveName;
    private string _countdownText = "";

    public CapturePromptModel(CaptureRequest r)
    {
        Request = r;
        _saveName = r.SuggestedName;
    }

    public CaptureRequest Request { get; }
    public string Title => "Keep your changes?";
    public string Lead => $"{(Request.TotalChanges == 1 ? "1 setting" : $"{Request.TotalChanges} settings")} changed while you played {Request.GameName}.";
    public IReadOnlyList<string> Lines => Request.Lines;
    public string MoreText => Request.More > 0 ? $"…and {Request.More} more" : "";

    public string Question => Request.ActiveSaveName is { } n
        ? $"Save updates your manual save “{n}” with these changes."
        : $"Save keeps them as a new save for {Request.DeviceName} and makes it the active setting.";

    public string SaveText => Request.ActiveSaveName is null ? "Save" : "Save (update “" + Request.ActiveSaveName + "”)";
    public string NeverNote => $"Not saving: “{Request.PresetLabel}” is applied again next launch. You can turn the prompt back on from the game's page.";

    public bool IsNaming { get => _isNaming; set => Set(ref _isNaming, value); }
    public string SaveName { get => _saveName; set => Set(ref _saveName, value); }
    public string CountdownText { get => _countdownText; set => Set(ref _countdownText, value); }
}

/// <summary>
/// The post-game prompt as a full-screen, topmost window: takes the foreground from the Xbox full-screen shell / Big Picture, answers to
/// A (save), Y (save as…), B (don't save), X (don't ask again), mouse, touch and keyboard (Enter / Esc), and closes itself without
/// saving after <see cref="TimeoutSeconds"/> seconds.
/// </summary>
public partial class CapturePromptWindow : Window
{
    public const int TimeoutSeconds = 120;

    private readonly CapturePromptModel _model;
    private readonly DispatcherTimer _pad = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _grab = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly bool[] _connected = new bool[XInput.MaxControllers];
    private DateTime _nextProbe = DateTime.MinValue;
    private GamepadButtons _previous;
    private bool _armed;           // buttons held when the window opened (the game's last A press) are ignored until released
    private bool _padUsed;
    private int _left = TimeoutSeconds;
    private int _grabTries;
    private bool _closing;

    public CapturePromptWindow(CaptureRequest request)
    {
        InitializeComponent();
        _model = new CapturePromptModel(request);
        DataContext = _model;
        FocusCue.Attach(this);
        Density.Attach(this);
        FocusCue.SetShow(this, true);
        UpdateCountdown();

        SourceInitialized += (_, _) => ForegroundWindow.Grab(new WindowInteropHelper(this).Handle);
        ContentRendered += (_, _) =>
        {
            Activate();
            SaveButton.Focus();
            Keyboard.Focus(SaveButton);
            _grab.Start();
        };
        _grab.Tick += (_, _) =>
        {
            if (IsActive || ++_grabTries > 8) { _grab.Stop(); return; }
            ForegroundWindow.Grab(new WindowInteropHelper(this).Handle);
        };
        _clock.Tick += (_, _) =>
        {
            if (--_left <= 0) Finish(new CaptureDecision(CaptureChoice.DontSave));
            else UpdateCountdown();
        };
        _pad.Tick += (_, _) => Poll();
        PreviewKeyDown += OnKey;
        PreviewMouseDown += (_, _) => FocusCue.SetShow(this, false);
        Loaded += (_, _) => { _clock.Start(); if (XInput.IsAvailable) _pad.Start(); };
        Closed += (_, _) => { _pad.Stop(); _clock.Stop(); _grab.Stop(); };
    }

    public CaptureDecision Decision { get; private set; } = new(CaptureChoice.DontSave);

    private void UpdateCountdown() =>
        _model.CountdownText = $"Closing without saving in {_left} s";

    private void Finish(CaptureDecision d)
    {
        if (_closing) return;
        _closing = true;
        Decision = d;
        Close();
    }

    // ---------------------------------------------------------------- answers

    private void OnSave(object sender, RoutedEventArgs e) => Finish(new CaptureDecision(CaptureChoice.Save));
    private void OnDontSave(object sender, RoutedEventArgs e) => Finish(new CaptureDecision(CaptureChoice.DontSave));
    private void OnNever(object sender, RoutedEventArgs e) => Finish(new CaptureDecision(CaptureChoice.DontAskAgain));
    private void OnSaveAs(object sender, RoutedEventArgs e) => BeginNaming();
    private void OnConfirmName(object sender, RoutedEventArgs e) => ConfirmName();
    private void OnBackFromName(object sender, RoutedEventArgs e) => EndNaming();
    private void OnKeyboard(object sender, RoutedEventArgs e) => OpenKeyboard();

    private void ConfirmName()
    {
        var name = _model.SaveName.Trim();
        Finish(new CaptureDecision(CaptureChoice.SaveAs, name.Length == 0 ? _model.Request.SuggestedName : name));
    }

    private void BeginNaming()
    {
        _model.IsNaming = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            NameBox.Focus();
            Keyboard.Focus(NameBox);
            NameBox.SelectAll();
            if (_padUsed) TouchKeyboard.Show(); // controller in hand: bring up the on-screen keyboard right away
        });
    }

    private void EndNaming()
    {
        _model.IsNaming = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { SaveButton.Focus(); Keyboard.Focus(SaveButton); });
    }

    private void OpenKeyboard()
    {
        NameBox.Focus();
        TouchKeyboard.Show();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        FocusCue.SetShow(this, true);
        if (e.Key == Key.Escape)
        {
            if (_model.IsNaming) EndNaming(); else Finish(new CaptureDecision(CaptureChoice.DontSave));
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _model.IsNaming)
        {
            ConfirmName();
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- gamepad (the app's XInput layer, polled while the window is open)

    private void Poll()
    {
        try
        {
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
            _padUsed = true;
            FocusCue.SetShow(this, true);

            if (_model.IsNaming)
            {
                if (pressed.HasFlag(GamepadButtons.A)) ConfirmName();
                else if (pressed.HasFlag(GamepadButtons.B)) EndNaming();
                else if (pressed.HasFlag(GamepadButtons.X)) OpenKeyboard();
                return;
            }
            if (pressed.HasFlag(GamepadButtons.A)) Finish(new CaptureDecision(CaptureChoice.Save));
            else if (pressed.HasFlag(GamepadButtons.Y)) BeginNaming();
            else if (pressed.HasFlag(GamepadButtons.B)) Finish(new CaptureDecision(CaptureChoice.DontSave));
            else if (pressed.HasFlag(GamepadButtons.X)) Finish(new CaptureDecision(CaptureChoice.DontAskAgain));
        }
        catch (Exception ex)
        {
            Log.Warn($"Capture prompt gamepad: {ex.Message}");
        }
    }
}

/// <summary>Takes the foreground from whatever is on top (the Xbox full-screen shell, Big Picture, the game's exit screen).</summary>
internal static class ForegroundWindow
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoMove = 0x2, SwpNoSize = 0x1, SwpShowWindow = 0x40;
    private const int SwRestore = 9;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    public static void Grab(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            AllowSetForegroundWindow(Environment.ProcessId);
            var fg = GetForegroundWindow();
            var fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
            var me = GetCurrentThreadId();
            var attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try
            {
                ShowWindow(hwnd, SwRestore);
                SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(me, fgThread, false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Taking the foreground failed: {ex.Message}");
        }
    }
}
