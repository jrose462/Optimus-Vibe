using System.Windows.Controls;

namespace GameProfiles.App.Views;

public partial class LibraryDetailView : UserControl
{
    public LibraryDetailView() => InitializeComponent();

    /// <summary>Configured game: the first preset tab; otherwise Play.</summary>
    public void FocusDefault(bool hasConfig)
    {
        if (hasConfig)
        {
            GameDetail.FocusSelectedTab();
            if (GameDetail.IsKeyboardFocusWithin) return;
        }
        else if (PlayButton.IsVisible && PlayButton.Focus()) return;
        BackButton.Focus();
    }
}
