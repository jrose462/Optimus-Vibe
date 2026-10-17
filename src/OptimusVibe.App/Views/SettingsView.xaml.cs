using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OptimusVibe.App.Services;
using OptimusVibe.App.ViewModels;

namespace OptimusVibe.App.Views;

public partial class SettingsView : UserControl
{
    private readonly List<SettingsSection> _sections = [];
    private readonly List<Button> _indexButtons = [];
    private bool _syncing;

    public SettingsView()
    {
        FocusRepoCommand = new RelayCommand(FocusRepo);
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SettingsViewModel old) old.SectionRequested -= OnSectionRequested;
            if (e.NewValue is SettingsViewModel vm) vm.SectionRequested += OnSectionRequested;
        };

        foreach (var s in SectionsPanel.Children.OfType<SettingsSection>())
        {
            _sections.Add(s);
            s.Expanded += OnExpanded;
            var section = s;
            var b = new Button
            {
                Style = (Style)FindResource("Button.Segment"),
                Content = string.IsNullOrEmpty(s.ShortName) ? s.Header : s.ShortName,
                Padding = new Thickness(14, 0, 14, 0),
            };
            b.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Go to " + s.Header);
            b.Click += (_, _) => ShowSection(section, focus: true);
            _indexButtons.Add(b);
            SectionIndex.Children.Add(b);
        }

        DependencyPropertyDescriptor.FromProperty(Density.IsCompactProperty, typeof(SettingsView))
            .AddValueChanged(this, (_, _) => NormalizeExpansion());
        Loaded += (_, _) => NormalizeExpansion();
    }

    private bool Compact => Density.GetIsCompact(this);

    // ---------------------------------------------------------------- sections

    /// <summary>Compact keeps exactly one section open; comfortable shows them all, so the flags don't matter there.</summary>
    private void NormalizeExpansion()
    {
        ContentColumn.MaxWidth = Compact ? double.PositiveInfinity : 1040; // compact: use the whole width
        if (Compact && _sections.Count > 0)
        {
            var keep = _sections.FirstOrDefault(s => s.IsExpanded) ?? _sections[0];
            _syncing = true;
            foreach (var s in _sections) s.IsExpanded = s == keep;
            _syncing = false;
        }
        UpdateIndex(null);
    }

    private void OnExpanded(SettingsSection opened)
    {
        if (_syncing || !Compact) { UpdateIndex(opened); return; }
        _syncing = true;
        foreach (var s in _sections) if (s != opened) s.IsExpanded = false;
        _syncing = false;
        UpdateIndex(opened);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => opened.BringIntoView());
    }

    private void UpdateIndex(SettingsSection? current)
    {
        current ??= CurrentSection();
        for (int i = 0; i < _indexButtons.Count; i++) _indexButtons[i].Tag = _sections[i] == current;
        if (current is not null && _sections.IndexOf(current) is var at and >= 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => _indexButtons[at].BringIntoView());
    }

    /// <summary>The section that holds the keyboard focus, else (compact) the open one.</summary>
    private SettingsSection? CurrentSection()
    {
        for (DependencyObject? d = Keyboard.FocusedElement as DependencyObject; d is not null;
             d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is SettingsSection s && _sections.Contains(s)) return s;
        return Compact ? _sections.FirstOrDefault(s => s.IsExpanded) : null;
    }

    /// <summary>Open (compact) and scroll to a section; optionally move focus onto it.</summary>
    public void ShowSection(SettingsSection section, bool focus)
    {
        if (Compact) section.IsExpanded = true;
        UpdateIndex(section);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            section.BringIntoView();
            if (focus) section.FocusHeader();
        });
    }

    /// <summary>LB / RB: the previous / next section.</summary>
    public void JumpSection(int delta)
    {
        if (_sections.Count == 0) return;
        var cur = CurrentSection();
        int i = cur is null ? (delta > 0 ? -1 : 0) : _sections.IndexOf(cur);
        ShowSection(_sections[(i + delta + _sections.Count) % _sections.Count], focus: true);
    }

    private void OnSectionRequested(string section)
    {
        // The page was just made visible (and re-scrolled to the top): scroll after that settles.
        SettingsSection? target = section switch { "updates" => UpdatesCard, "account" => AccountCard, "appearance" => AppearanceCard, "devices" => DevicesCard, "gamelab" => LabCard, "repo" => RepoCard, _ => null };
        if (target is not null) ShowSection(target, focus: false);
    }

    /// <summary>"GitHub account" choice: jump to the repo picker (or sign-in when signed out).</summary>
    public ICommand FocusRepoCommand { get; }

    private void FocusRepo()
    {
        ShowSection(RepoCard, focus: false);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            UpdateLayout();
            if (RepoSearch.IsVisible) RepoSearch.Focus();
        });
    }

    public void FocusDefault()
    {
        Scroller.ScrollToTop();
        (Compact ? CompactBackButton : BackButton).Focus();
    }
}
