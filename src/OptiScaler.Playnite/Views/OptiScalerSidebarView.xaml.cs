using System.Windows.Controls;

namespace OptiScaler.Playnite.Views
{
    public partial class OptiScalerSidebarView : UserControl
    {
        public OptiScalerSidebarView(OptiScalerSidebarViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
