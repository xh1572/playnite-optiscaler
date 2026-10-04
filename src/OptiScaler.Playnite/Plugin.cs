using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;
using OptiScaler.Playnite.Core.Services;
using OptiScaler.Playnite.Playnite;
using OptiScaler.Playnite.Views;

namespace OptiScaler.Playnite
{
    public sealed class OptiScalerPlugin : GenericPlugin
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private readonly string pluginDataPath;
        private readonly OptiScalerSettingsViewModel settings;
        private readonly PlayniteGameCatalog catalog;
        private readonly InstallationService installationService;
        private readonly ReleaseService releaseService;
        private readonly ProfileService profileService;
        private OptiScalerSidebarViewModel sidebarViewModel;
        private OptiScalerSidebarView sidebarView;
        private SidebarItem sidebarItem;

        public override Guid Id { get; } = Guid.Parse("1d4f96d1-ea52-4f70-9b3d-e2ec0a7d5f70");
        public IPlayniteAPI Api => PlayniteApi;

        public OptiScalerPlugin(IPlayniteAPI api) : base(api)
        {
            pluginDataPath = GetPluginUserDataPath();
            settings = new OptiScalerSettingsViewModel(this);
            catalog = new PlayniteGameCatalog(api, pluginDataPath);
            installationService = new InstallationService(pluginDataPath);
            releaseService = new ReleaseService(pluginDataPath);
            profileService = new ProfileService(pluginDataPath);
            Properties = new GenericPluginProperties { HasSettings = true };
        }

        public override IEnumerable<SidebarItem> GetSidebarItems()
        {
            if (sidebarItem != null) return new[] { sidebarItem };
            sidebarItem = new SidebarItem
            {
                Type = SiderbarItemType.View,
                Title = "OptiScaler 管理",
                Visible = true,
                Icon = CreateIcon(),
                Opened = OpenSidebar,
                Closed = () => { }
            };
            return new[] { sidebarItem };
        }

        public override ISettings GetSettings(bool firstRunSettings) => settings;

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new SettingsView { DataContext = settings };
        }

        public string SelectPackagePath()
        {
            return PlayniteApi.Dialogs.SelectFile("OptiScaler 安装包 (*.zip;*.7z)|*.zip;*.7z|所有文件 (*.*)|*.*");
        }

        public bool EditGameList()
        {
            var entries = catalog.ReadGames(true, false);
            var editor = new GameSelectionEditorWindow(entries, settings.Settings.UseCustomGameList, settings.Settings.ManagedGameIds)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            if (editor.ShowDialog() != true) return false;

            settings.Settings.UseCustomGameList = editor.UseCustomList;
            settings.Settings.ManagedGameIds = editor.SelectedGameIds.ToList();
            settings.EndEdit();
            return true;
        }

        public void EditConfiguration(Core.Models.ManagedGame game, InstallationService service)
        {
            var contents = service.ReadConfiguration(game);
            var editor = new ConfigurationEditorWindow(game.Name, contents, profileService)
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            if (editor.ShowDialog() == true) service.SaveConfiguration(game, editor.ConfigurationText);
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            var roots = new List<string>();
            try
            {
                if (PlayniteApi.Database != null && PlayniteApi.Database.IsOpen)
                    roots.AddRange(PlayniteApi.Database.Games
                        .Where(x => x != null && x.IsInstalled && !string.IsNullOrWhiteSpace(x.InstallDirectory))
                        .Select(x => x.InstallDirectory));
            }
            catch (Exception ex) { Logger.Warn(ex, "Failed to collect game directories for OptiScaler recovery."); }
            Task.Run(() => RecoverIncompleteOperations(roots));
        }

        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
            RefreshView();
        }

        public override void OnGameStarted(OnGameStartedEventArgs args) => RefreshView();
        public override void OnGameStopped(OnGameStoppedEventArgs args) => RefreshView();
        public override void OnGameInstalled(OnGameInstalledEventArgs args) => RefreshView();
        public override void OnGameUninstalled(OnGameUninstalledEventArgs args) => RefreshView();

        public override void Dispose()
        {
            if (sidebarViewModel != null) sidebarViewModel.Dispose();
            else releaseService.Dispose();
            base.Dispose();
        }

        private Control OpenSidebar()
        {
            if (sidebarViewModel == null)
            {
                sidebarViewModel = new OptiScalerSidebarViewModel(this, catalog, installationService, releaseService, profileService, settings);
                sidebarView = new OptiScalerSidebarView(sidebarViewModel);
            }
            sidebarViewModel.Refresh();
            return sidebarView;
        }

        private void RefreshView()
        {
            if (sidebarViewModel == null) return;
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null) dispatcher.BeginInvoke(new Action(() => sidebarViewModel.Refresh(true)));
                else sidebarViewModel.Refresh(true);
            }
            catch (Exception ex) { Logger.Warn(ex, "Failed to refresh OptiScaler sidebar."); }
        }

        private void RecoverIncompleteOperations(IEnumerable<string> gameRoots)
        {
            try
            {
                foreach (var gameRoot in gameRoots ?? Enumerable.Empty<string>())
                {
                    try { installationService.RecoverIncomplete(gameRoot); } catch (Exception ex) { Logger.Warn(ex, "Failed to recover OptiScaler operation for " + gameRoot); }
                }
            }
            catch (Exception ex) { Logger.Warn(ex, "Failed to scan for incomplete OptiScaler operations."); }
        }

        private static BitmapImage CreateIcon()
        {
            var assemblyDirectory = Path.GetDirectoryName(typeof(OptiScalerPlugin).Assembly.Location);
            var iconPath = string.IsNullOrWhiteSpace(assemblyDirectory) ? null : Path.Combine(assemblyDirectory, "icon.png");
            if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath)) return null;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(iconPath, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch { return null; }
        }
    }
}
