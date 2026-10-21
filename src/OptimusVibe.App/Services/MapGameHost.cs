using System.Windows;
using OptimusVibe.App.ViewModels;
using OptimusVibe.App.Views;
using OptimusVibe.Logging;

namespace OptimusVibe.App.Services;

/// <summary>Opens the full-screen "Map this game" window for a model (on the UI thread).</summary>
public static class MapGameHost
{
    public static void Open(MapGameModel model)
    {
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.Invoke(() =>
        {
            try
            {
                new MapGameWindow(model).Show();
            }
            catch (Exception ex)
            {
                Log.Error("Map this game window failed", ex);
            }
        });
    }
}
