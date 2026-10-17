using System.Windows;
using OptimusVibe.App.Views;
using OptimusVibe.Launch;
using OptimusVibe.Logging;

namespace OptimusVibe.App.Services;

/// <summary>
/// The app's <see cref="ICapturePrompt"/>: a full-screen, themed WPF window that works in the headless <c>run</c> path too (the app exe
/// hosts WPF). Falls back to the message box if the window can't be shown.
/// </summary>
public sealed class WpfCapturePrompt : ICapturePrompt
{
    private readonly ICapturePrompt _fallback = new MessageBoxCapturePrompt();

    public CaptureDecision Ask(CaptureRequest request)
    {
        var app = Application.Current;
        if (app is null) return _fallback.Ask(request);
        try
        {
            return app.Dispatcher.CheckAccess() ? Show(request) : app.Dispatcher.Invoke(() => Show(request));
        }
        catch (Exception ex)
        {
            Log.Error("Capture window failed, using a message box", ex);
            return _fallback.Ask(request);
        }
    }

    private static CaptureDecision Show(CaptureRequest request)
    {
        // The headless run path starts before the themed main window: apply this machine's theme first.
        try { ThemeManager.Apply(SettingsStore.Load().Theme); }
        catch (Exception ex) { Log.Warn($"Theme not applied: {ex.Message}"); }

        var window = new CapturePromptWindow(request);
        window.ShowDialog();
        return window.Decision;
    }
}
