using System.Windows.Input;
using OptimusVibe.GameLab;

namespace OptimusVibe.App.ViewModels;

/// <summary>A button of a checklist row: the text, the existing command behind it, and the controller glyph (A on the main one, B on a cancel).</summary>
/// <param name="Primary">The main button of the current step (filled, shown with the A glyph when a gamepad is connected).</param>
/// <param name="Glyph">"A" or "B" or null.</param>
public sealed record StepButton(string Text, ICommand Command, bool Primary, string? Glyph, string Name, string? Tip)
{
    public bool ShowA => Glyph == "A";
    public bool ShowB => Glyph == "B";
}

/// <summary>
/// One row of the guided checklist. Done: one line with its summary and a check. Current: expanded, with its detail and buttons. Upcoming: one
/// dim line. The fix step also lists what the game changed on demand ("See which") and offers "Not now" / "Don't ask again".
/// </summary>
/// <param name="ShowFixExtras">The fix step's "See which" and its list.</param>
/// <param name="ShowSnooze">"Not now" and "Don't ask again" (a snoozable session card of the game is pending).</param>
public sealed record StepRow(int Number, LabStepId Id, StepState State, string Title, string Detail, string Summary, StepButton? Primary,
    IReadOnlyList<StepButton> Secondary, bool ShowFixExtras, bool ShowSnooze)
{
    public bool IsDone => State == StepState.Done;
    public bool IsCurrent => State == StepState.Current;
    public bool IsUpcoming => State == StepState.Upcoming;
    public bool IsFix => ShowFixExtras;
    public bool HasPrimary => Primary is not null;
    public bool HasSecondary => Secondary.Count > 0;
    public bool HasButtons => HasPrimary || HasSecondary || ShowFixExtras;

    /// <summary>What a row looks like: the panel only swaps its row list when this changes, so a refresh never rebuilds buttons under the player's focus.</summary>
    public string Key =>
        $"{Number}|{State}|{Title}|{Detail}|{Summary}|{Primary?.Text}/{Primary?.Command.GetHashCode()}|{string.Join(',', Secondary.Select(s => s.Text))}|{ShowFixExtras}|{ShowSnooze}";
}
