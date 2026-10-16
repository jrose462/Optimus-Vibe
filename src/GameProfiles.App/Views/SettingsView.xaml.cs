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
