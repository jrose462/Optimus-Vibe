using OptimusVibe.App.Input;

namespace OptimusVibe.App.ViewModels;

/// <summary>One controller hint: the button (or the pair, like LB RB) and what it does.</summary>
public sealed record PadHint(PadButton First, PadButton? Second, string Text)
{
    public PadHint(PadButton button, string text) : this(button, null, text) { }
}
