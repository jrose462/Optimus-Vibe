using System.Windows;

namespace OptimusVibe.App.Input;

/// <summary>The controller buttons the UI can show a glyph for (Views\ButtonGlyph).</summary>
public enum PadButton { A, B, X, Y, LB, RB, LT, RT, Menu, View, DPad, LStick, RStick }

/// <summary>Small / medium / large glyph: 20 / 26 / 32 DIPs high.</summary>
public enum GlyphSize { Small, Medium, Large }

/// <summary>
/// PadState.IsConnected (inherited): true while a gamepad is connected. The main window sets it from the XInput layer; button
/// glyphs only show while it is true (except where they ask to be <c>Always</c> shown, like the post-game prompt).
/// </summary>
public static class PadState
{
    public static readonly DependencyProperty IsConnectedProperty = DependencyProperty.RegisterAttached(
        "IsConnected", typeof(bool), typeof(PadState),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsConnected(DependencyObject d) => (bool)d.GetValue(IsConnectedProperty);
    public static void SetIsConnected(DependencyObject d, bool value) => d.SetValue(IsConnectedProperty, value);
}
