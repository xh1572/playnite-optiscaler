using System.Windows;
using System.Windows.Controls;

namespace OptiScaler.Playnite.Views
{
    public partial class HelpLabel : UserControl
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(HelpLabel), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
            nameof(Description), typeof(string), typeof(HelpLabel), new PropertyMetadata(string.Empty));

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public string Description
        {
            get => (string)GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        // Foreground and FontSize come from the implicit HelpLabel style in Theme.xaml, or are
        // inherited from the parent; setting them here would override that style.
        public HelpLabel() => InitializeComponent();
    }
}
