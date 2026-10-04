using System.Windows.Controls;

namespace OptiScaler.Playnite.Views
{
    public partial class SettingsView : UserControl
    {
        public SettingsView() => InitializeComponent();

        private void BrowsePackage_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            (DataContext as OptiScalerSettingsViewModel)?.ChoosePackage();
        }
    }
}
