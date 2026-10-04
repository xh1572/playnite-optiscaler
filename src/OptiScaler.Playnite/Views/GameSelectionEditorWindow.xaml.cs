using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using OptiScaler.Playnite.Playnite;

namespace OptiScaler.Playnite.Views
{
    public sealed class GameSelectionItem : INotifyPropertyChanged
    {
        private bool isSelected;

        public string GameId { get; }
        public string Name { get; }
        public string InstallDirectory { get; }
        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (isSelected == value) return;
                isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public GameSelectionItem(PlayniteGameEntry entry, bool isSelected)
        {
            GameId = entry.ManagedGame.Id.ToString("D");
            Name = string.IsNullOrWhiteSpace(entry.ManagedGame.Name) ? "未命名游戏" : entry.ManagedGame.Name;
            InstallDirectory = entry.ManagedGame.InstallDirectory ?? string.Empty;
            this.isSelected = isSelected;
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public partial class GameSelectionEditorWindow : Window, INotifyPropertyChanged
    {
        private string searchText = string.Empty;
        private bool useCustomList;
        private readonly ICollectionView gamesView;

        public ObservableCollection<GameSelectionItem> Games { get; } = new ObservableCollection<GameSelectionItem>();
        public ICollectionView GamesView => gamesView;
        public string SearchText
        {
            get => searchText;
            set
            {
                var normalized = value ?? string.Empty;
                if (string.Equals(searchText, normalized, StringComparison.Ordinal)) return;
                searchText = normalized;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchText)));
                gamesView.Refresh();
            }
        }
        public bool UseCustomList
        {
            get => useCustomList;
            set
            {
                if (useCustomList == value) return;
                useCustomList = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UseCustomList)));
            }
        }
        public string SelectionSummary => "已选择 " + Games.Count(x => x.IsSelected) + " / " + Games.Count;
        public IReadOnlyList<string> SelectedGameIds => Games.Where(x => x.IsSelected).Select(x => x.GameId).ToList();

        public GameSelectionEditorWindow(IEnumerable<PlayniteGameEntry> entries, bool useCustomList, IEnumerable<string> selectedGameIds)
        {
            InitializeComponent();
            DarkTitleBar.Attach(this);
            UseCustomList = useCustomList;
            var selected = new HashSet<string>(selectedGameIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in (entries ?? Enumerable.Empty<PlayniteGameEntry>()).OrderBy(x => x.ManagedGame.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new GameSelectionItem(entry, !useCustomList || selected.Contains(entry.ManagedGame.Id.ToString("D")));
                item.PropertyChanged += SelectionItem_PropertyChanged;
                Games.Add(item);
            }
            gamesView = CollectionViewSource.GetDefaultView(Games);
            gamesView.Filter = FilterGame;
            DataContext = this;
        }

        private void SelectionItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GameSelectionItem.IsSelected))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionSummary)));
        }

        private bool FilterGame(object value)
        {
            var game = value as GameSelectionItem;
            if (game == null) return false;
            if (string.IsNullOrWhiteSpace(SearchText)) return true;
            return game.Name.IndexOf(SearchText, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                   game.InstallDirectory.IndexOf(SearchText, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var game in Games) game.IsSelected = true;
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            foreach (var game in Games) game.IsSelected = false;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
