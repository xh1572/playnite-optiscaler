using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using OptiScaler.Playnite.Core.Services;

namespace OptiScaler.Playnite.Views
{
    public partial class ConfigurationEditorWindow : Window, INotifyPropertyChanged
    {
        private IniDocumentViewModel document;
        private string initialText;
        private readonly ProfileService profileService;
        public string ConfigurationText { get; private set; }
        public ObservableCollection<string> ProfileNames { get; } = new ObservableCollection<string>();
        private string selectedProfile;
        private string profileName;
        public string SelectedProfile
        {
            get => selectedProfile;
            set { if (string.Equals(selectedProfile, value, StringComparison.Ordinal)) return; selectedProfile = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedProfile))); }
        }
        public string ProfileName
        {
            get => profileName;
            set { if (string.Equals(profileName, value, StringComparison.Ordinal)) return; profileName = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProfileName))); }
        }

        public ConfigurationEditorWindow(string gameName, string contents, ProfileService profileService = null)
        {
            InitializeComponent();
            DarkTitleBar.Attach(this);
            Title = "OptiScaler.ini 配置 - " + gameName;
            this.profileService = profileService;
            initialText = contents ?? string.Empty;
            document = IniDocumentViewModel.Parse(initialText);
            DataContext = document;
            ConfigurationTextBox.Text = initialText;
            RefreshProfiles();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            // If the raw tab was edited, preserve it verbatim; otherwise serialize visual edits
            // back into the original section/key layout and comments.
            ConfigurationText = string.Equals(ConfigurationTextBox.Text, initialText, System.StringComparison.Ordinal)
                ? document.ToText()
                : ConfigurationTextBox.Text;
            DialogResult = true;
        }

        private void LoadProfile_Click(object sender, RoutedEventArgs e)
        {
            if (profileService == null || string.IsNullOrWhiteSpace(SelectedProfile)) return;
            try
            {
                var text = profileService.Load(SelectedProfile);
                SetConfigurationText(text);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "OptiScaler profile", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveProfile_Click(object sender, RoutedEventArgs e)
        {
            if (profileService == null || string.IsNullOrWhiteSpace(ProfileName)) return;
            try
            {
                profileService.Save(ProfileName, GetCurrentText());
                RefreshProfiles();
                SelectedProfile = ProfileName.Trim();
                ProfileName = SelectedProfile;
                DataContext = null;
                DataContext = document;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "OptiScaler profile", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            if (profileService == null || string.IsNullOrWhiteSpace(SelectedProfile)) return;
            if (MessageBox.Show(this, "确定删除配置 profile“" + SelectedProfile + "”吗？", "OptiScaler profile", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                profileService.Delete(SelectedProfile);
                RefreshProfiles();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "OptiScaler profile", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string GetCurrentText()
        {
            return string.Equals(ConfigurationTextBox.Text, initialText, StringComparison.Ordinal)
                ? document.ToText()
                : ConfigurationTextBox.Text;
        }

        private void SetConfigurationText(string text)
        {
            initialText = text ?? string.Empty;
            document = IniDocumentViewModel.Parse(initialText);
            ConfigurationTextBox.Text = initialText;
            DataContext = document;
            RefreshProfiles();
        }

        private void RefreshProfiles()
        {
            ProfileNames.Clear();
            if (profileService == null) return;
            foreach (var name in profileService.ListProfiles()) ProfileNames.Add(name);
            if (!string.IsNullOrWhiteSpace(SelectedProfile) && ProfileNames.Contains(SelectedProfile)) return;
            SelectedProfile = ProfileNames.FirstOrDefault();
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
