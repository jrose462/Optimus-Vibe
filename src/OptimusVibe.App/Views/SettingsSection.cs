using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace OptimusVibe.App.Views;

/// <summary>
/// One Settings card. Comfortable: a plain card with its title, always open. Compact: the title is a button that folds the card
/// (the page keeps one section open at a time and offers an index to jump between them).
/// </summary>
public sealed class SettingsSection : HeaderedContentControl
{
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(SettingsSection),
        new FrameworkPropertyMetadata(true, (d, e) => { if (e.NewValue is true) ((SettingsSection)d).Expanded?.Invoke((SettingsSection)d); }));

    /// <summary>The short name on the section index ("Account", "Look").</summary>
    public static readonly DependencyProperty ShortNameProperty = DependencyProperty.Register(
        nameof(ShortName), typeof(string), typeof(SettingsSection), new PropertyMetadata(""));

    private ToggleButton? _header;

    static SettingsSection()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SettingsSection), new FrameworkPropertyMetadata(typeof(SettingsSection)));
        FocusableProperty.OverrideMetadata(typeof(SettingsSection), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(SettingsSection), new FrameworkPropertyMetadata(false));
    }

    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public string ShortName { get => (string)GetValue(ShortNameProperty); set => SetValue(ShortNameProperty, value); }

    /// <summary>Raised when the section was opened.</summary>
    public event Action<SettingsSection>? Expanded;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _header = GetTemplateChild("HeaderButton") as ToggleButton;
    }

    /// <summary>Compact: focus the title button. Comfortable: the first control inside.</summary>
    public bool FocusHeader()
    {
        ApplyTemplate();
        if (_header is { IsVisible: true } h) return h.Focus();
        return MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
}
