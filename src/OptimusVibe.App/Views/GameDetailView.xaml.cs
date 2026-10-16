using System.Windows.Controls;

namespace OptimusVibe.App.Views;

public partial class GameDetailView : UserControl
{
    public GameDetailView() => InitializeComponent();

    /// <summary>After LB/RB, put focus on the selected tab header so D-pad Down enters its content.</summary>
    public void FocusSelectedTab()
    {
        if (Tabs.ItemContainerGenerator.ContainerFromIndex(Tabs.SelectedIndex) is TabItem tab) tab.Focus();
    }
}
