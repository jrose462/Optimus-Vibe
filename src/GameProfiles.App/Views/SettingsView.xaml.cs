using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameProfiles.App.ViewModels;

namespace GameProfiles.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        FocusRepoCommand = new RelayCommand(FocusRepo);
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SettingsViewModel old) old.SectionRequested -= OnSectionRequested;
            if (e.NewValue is SettingsViewModel vm) vm.SectionRequested += OnSectionRequested;
        };
    }

    private void OnSectionRequested(string section)
    {
        // The page was just made visible (and re-scrolled to the top): scroll after that settles.
        FrameworkElement? target = section switch { "updates" => UpdatesCard, "account" => AccountCard, _ => null };
        target?.BringIntoView();
    }

    /// <summary>"GitHub account" choice: jump to the repo picker (or sign-in when signed out).</summary>
    public ICommand FocusRepoCommand { get; }

    private void FocusRepo()
    {
        RepoCard.BringIntoView();
        if (RepoSearch.IsVisible) RepoSearch.Focus();
    }

    public void FocusDefault()
    {
        Scroller.ScrollToTop();
        BackButton.Focus();
    }
}
