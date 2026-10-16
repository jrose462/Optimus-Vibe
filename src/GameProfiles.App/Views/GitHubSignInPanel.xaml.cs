using System.ComponentModel;
using System.Windows.Controls;
using GameProfiles.App.ViewModels;

namespace GameProfiles.App.Views;

public partial class GitHubSignInPanel : UserControl
{
    public GitHubSignInPanel()
    {
        InitializeComponent();
        // PasswordBox can't bind: push the token into the view model, and clear the box when the VM clears it.
        PatBox.PasswordChanged += (_, _) =>
        {
            if (DataContext is GitHubAccountViewModel vm) vm.Pat = PatBox.Password;
        };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged o) o.PropertyChanged -= OnVmChanged;
            if (e.NewValue is INotifyPropertyChanged n) n.PropertyChanged += OnVmChanged;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GitHubAccountViewModel.Pat) && sender is GitHubAccountViewModel { Pat: "" } && PatBox.Password.Length > 0)
            PatBox.Clear();
    }
}
