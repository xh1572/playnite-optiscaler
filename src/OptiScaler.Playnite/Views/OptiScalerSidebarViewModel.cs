using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using OptiScaler.Playnite.Core.Models;
using OptiScaler.Playnite.Core.Services;
using OptiScaler.Playnite.Playnite;

namespace OptiScaler.Playnite.Views
{
    public sealed class DelegateCommand : ICommand
    {
        private readonly Action execute;
        private readonly Func<bool> canExecute;
        public DelegateCommand(Action execute, Func<bool> canExecute = null) { this.execute = execute; this.canExecute = canExecute; }
        public bool CanExecute(object parameter) => canExecute == null || canExecute();
        public void Execute(object parameter) => execute();
        public event EventHandler CanExecuteChanged;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public sealed class GameRowViewModel : INotifyPropertyChanged
    {
        private OptiScalerStatus status;
        private string dx11Upscaler = "auto";
        private string dx12Upscaler = "auto";
        private string vulkanUpscaler = "auto";
        private string frameGeneration = "auto";
        private string frameGenerationInput = "auto";
        private string frameGenerationOutput = "auto";
        private string loadAsiPlugins = "auto";
        private string outputScaling = "auto";
        private string outputScalingMultiplier = "auto";
        private string outputScalingDownscaler = "auto";
        private string upscaleRatioEnabled = "auto";
        private string upscaleRatioValue = "auto";
        private string hudFix = "auto";
        private string fsr4Update = "auto";
        private string fsr4Model = "auto";
        private string gpuSpoofing = "auto";
        private string injectionMethod;
        private readonly string defaultInjectionMethod;
        private string profileName = string.Empty;
        public PlayniteGameEntry Entry { get; }
        public string Name => Entry.ManagedGame.Name;
        public string InstallDirectory => Entry.ManagedGame.InstallDirectory;
        public string StatusText => status == null ? "未知" : GetStatusText(status);
        public string VersionText => string.IsNullOrWhiteSpace(status?.Version) ? "版本未知" : status.Version;
        public string DetailsText => status?.Message ?? string.Empty;
        public string InjectionText => string.IsNullOrWhiteSpace(InjectionMethod) ? "注入方式：未知" : "注入方式：" + InjectionMethod;
        public IEnumerable<string> Components => status?.Components ?? Enumerable.Empty<string>();
        public string Dx11Upscaler { get => dx11Upscaler; set { dx11Upscaler = value ?? "auto"; OnPropertyChanged(nameof(Dx11Upscaler)); } }
        public string Dx12Upscaler { get => dx12Upscaler; set { dx12Upscaler = value ?? "auto"; OnPropertyChanged(nameof(Dx12Upscaler)); } }
        public string VulkanUpscaler { get => vulkanUpscaler; set { vulkanUpscaler = value ?? "auto"; OnPropertyChanged(nameof(VulkanUpscaler)); } }
        public string FrameGeneration { get => frameGeneration; set { frameGeneration = value ?? "auto"; OnPropertyChanged(nameof(FrameGeneration)); } }
        public string FrameGenerationInput { get => frameGenerationInput; set { frameGenerationInput = value ?? "auto"; OnPropertyChanged(nameof(FrameGenerationInput)); } }
        public string FrameGenerationOutput { get => frameGenerationOutput; set { frameGenerationOutput = value ?? "auto"; OnPropertyChanged(nameof(FrameGenerationOutput)); } }
        public string LoadAsiPlugins { get => loadAsiPlugins; set { loadAsiPlugins = value ?? "auto"; OnPropertyChanged(nameof(LoadAsiPlugins)); } }
        public string OutputScaling { get => outputScaling; set { outputScaling = value ?? "auto"; OnPropertyChanged(nameof(OutputScaling)); } }
        public string OutputScalingMultiplier { get => outputScalingMultiplier; set { outputScalingMultiplier = value ?? "auto"; OnPropertyChanged(nameof(OutputScalingMultiplier)); } }
        public string OutputScalingDownscaler { get => outputScalingDownscaler; set { outputScalingDownscaler = value ?? "auto"; OnPropertyChanged(nameof(OutputScalingDownscaler)); } }
        public string UpscaleRatioEnabled { get => upscaleRatioEnabled; set { upscaleRatioEnabled = value ?? "auto"; OnPropertyChanged(nameof(UpscaleRatioEnabled)); } }
        public string UpscaleRatioValue { get => upscaleRatioValue; set { upscaleRatioValue = value ?? "auto"; OnPropertyChanged(nameof(UpscaleRatioValue)); } }
        public string HudFix { get => hudFix; set { hudFix = value ?? "auto"; OnPropertyChanged(nameof(HudFix)); } }
        public string Fsr4Update { get => fsr4Update; set { fsr4Update = value ?? "auto"; OnPropertyChanged(nameof(Fsr4Update)); } }
        public string Fsr4Model { get => fsr4Model; set { fsr4Model = value ?? "auto"; OnPropertyChanged(nameof(Fsr4Model)); } }
        public string GpuSpoofing { get => gpuSpoofing; set { gpuSpoofing = value ?? "auto"; OnPropertyChanged(nameof(GpuSpoofing)); } }
        public string ProfileName { get => profileName; set { profileName = value ?? string.Empty; OnPropertyChanged(nameof(ProfileName)); } }
        public string InjectionMethod { get => injectionMethod; set { injectionMethod = value ?? defaultInjectionMethod; OnPropertyChanged(nameof(InjectionMethod)); OnPropertyChanged(nameof(InjectionText)); } }
        public bool CanEditConfiguration => status != null && status.IsInstalled && status.Manifest != null && !string.IsNullOrWhiteSpace(status.ConfigurationPath);
        public bool IsInstalled => status != null && status.IsInstalled;
        public OptiScalerStatus Status => status;
        public bool HasFsr4Swap => status != null && status.HasFsr4Swap;
        public string Fsr4SwapText => HasFsr4Swap
            ? "已替换为 " + (status.Fsr4SwapVersion ?? "Extras 版本") + "：" + string.Join("、", status.Manifest.Fsr4SwapFiles.Select(Path.GetFileName))
            : "未替换。替换后可随时还原为原始文件。";
        public string IconPath => Entry.ManagedGame.IconPath;
        public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : StringInfo.GetNextTextElement(Name.Trim()).ToUpperInvariant();
        public bool HasComponents => Components.Any();
        // Drives the status badge color in the sidebar.
        public string StatusKind
        {
            get
            {
                switch (status?.State)
                {
                    case OptiScalerInstallState.Installed: return "Installed";
                    case OptiScalerInstallState.UpdateAvailable: return "Update";
                    case OptiScalerInstallState.Conflict:
                    case OptiScalerInstallState.Incomplete:
                    case OptiScalerInstallState.MissingDirectory: return "Attention";
                    default: return "None";
                }
            }
        }

        public GameRowViewModel(PlayniteGameEntry entry, string defaultInjectionMethod = null)
        {
            Entry = entry;
            status = entry.Status;
            this.defaultInjectionMethod = string.IsNullOrWhiteSpace(defaultInjectionMethod) ? "dxgi.dll" : defaultInjectionMethod;
            injectionMethod = string.IsNullOrWhiteSpace(status?.InjectionMethod) ? this.defaultInjectionMethod : status.InjectionMethod;
            profileName = status?.Manifest?.AppliedProfileName ?? string.Empty;
        }
        public void UpdateStatus(OptiScalerStatus value)
        {
            status = value;
            if (!string.IsNullOrWhiteSpace(value?.InjectionMethod)) injectionMethod = value.InjectionMethod;
            if (!string.IsNullOrWhiteSpace(value?.Manifest?.AppliedProfileName)) profileName = value.Manifest.AppliedProfileName;
            OnPropertyChanged(string.Empty);
        }

        public void MarkUpdateAvailable(string latestVersion)
        {
            if (status == null || (status.State != OptiScalerInstallState.Installed && status.State != OptiScalerInstallState.UpdateAvailable) || string.IsNullOrWhiteSpace(status.Version) || string.IsNullOrWhiteSpace(latestVersion)) return;
            if (VersionsMatch(status.Version, latestVersion))
            {
                if (status.State == OptiScalerInstallState.UpdateAvailable)
                {
                    status.State = OptiScalerInstallState.Installed;
                    status.Message = "由本插件管理。";
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(StatusKind));
                    OnPropertyChanged(nameof(DetailsText));
                }
                return;
            }
            status.State = OptiScalerInstallState.UpdateAvailable;
            status.Message = "当前版本：" + status.Version + "；可更新到：" + latestVersion;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusKind));
            OnPropertyChanged(nameof(DetailsText));
        }

        private static bool VersionsMatch(string current, string latest)
        {
            var a = NormalizeVersion(current);
            var b = NormalizeVersion(latest);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
                   a.StartsWith(b + ".", StringComparison.OrdinalIgnoreCase) ||
                   b.StartsWith(a + ".", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeVersion(string value)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);
            var separator = text.IndexOfAny(new[] { ' ', '(' });
            if (separator >= 0) text = text.Substring(0, separator);
            return text.Trim();
        }

        public void LoadQuickConfiguration(string contents)
        {
            var document = IniDocumentViewModel.Parse(contents);
            Dx11Upscaler = document.GetValue("Upscalers", "Dx11Upscaler");
            Dx12Upscaler = document.GetValue("Upscalers", "Dx12Upscaler");
            VulkanUpscaler = document.GetValue("Upscalers", "VulkanUpscaler");
            FrameGeneration = document.GetValue("FrameGen", "Enabled");
            FrameGenerationInput = document.GetValue("FrameGen", "FGInput");
            FrameGenerationOutput = document.GetValue("FrameGen", "FGOutput");
            LoadAsiPlugins = document.GetValue("Plugins", "LoadAsiPlugins");
            OutputScaling = document.GetValue("OutputScaling", "Enabled");
            OutputScalingMultiplier = document.GetValue("OutputScaling", "Multiplier");
            OutputScalingDownscaler = document.GetValue("OutputScaling", "Downscaler");
            UpscaleRatioEnabled = document.GetValue("UpscaleRatio", "UpscaleRatioOverrideEnabled");
            UpscaleRatioValue = document.GetValue("UpscaleRatio", "UpscaleRatioOverrideValue");
            HudFix = document.GetValue("OptiFG", "HUDFix");
            Fsr4Update = document.GetValue("FSR", "Fsr4Update");
            Fsr4Model = document.GetValue("FSR", "Fsr4Model");
            GpuSpoofing = document.GetValue("Spoofing", "Dxgi");
        }

        public void SaveQuickConfiguration(string contents)
        {
            var document = IniDocumentViewModel.Parse(contents);
            document.SetValue("Upscalers", "Dx11Upscaler", Dx11Upscaler);
            document.SetValue("Upscalers", "Dx12Upscaler", Dx12Upscaler);
            document.SetValue("Upscalers", "VulkanUpscaler", VulkanUpscaler);
            document.SetValue("FrameGen", "Enabled", FrameGeneration);
            document.SetValue("FrameGen", "FGInput", FrameGenerationInput);
            document.SetValue("FrameGen", "FGOutput", FrameGenerationOutput);
            document.SetValue("Plugins", "LoadAsiPlugins", LoadAsiPlugins);
            document.SetValue("OutputScaling", "Enabled", OutputScaling);
            document.SetValue("OutputScaling", "Multiplier", OutputScalingMultiplier);
            document.SetValue("OutputScaling", "Downscaler", OutputScalingDownscaler);
            document.SetValue("UpscaleRatio", "UpscaleRatioOverrideEnabled", UpscaleRatioEnabled);
            document.SetValue("UpscaleRatio", "UpscaleRatioOverrideValue", UpscaleRatioValue);
            document.SetValue("OptiFG", "HUDFix", HudFix);
            document.SetValue("FSR", "Fsr4Update", Fsr4Update);
            document.SetValue("FSR", "Fsr4Model", Fsr4Model);
            document.SetValue("Spoofing", "Dxgi", GpuSpoofing);
            // Return the serialized document through the caller to preserve the existing service boundary.
            QuickConfigurationText = document.ToText();
        }

        public string QuickConfigurationText { get; private set; }
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private static string GetStatusText(OptiScalerStatus value)
        {
            switch (value.State)
            {
                case OptiScalerInstallState.Installed: return "已安装";
                case OptiScalerInstallState.UpdateAvailable: return "有可用更新";
                case OptiScalerInstallState.Conflict: return "文件冲突";
                case OptiScalerInstallState.Incomplete: return "安装不完整";
                case OptiScalerInstallState.MissingDirectory: return "安装目录不存在";
                case OptiScalerInstallState.NotInstalled: return "未安装";
                default: return "未知";
            }
        }
    }

    public sealed class OptiScalerSidebarViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly OptiScalerPlugin plugin;
        private readonly PlayniteGameCatalog catalog;
        private readonly InstallationService installationService;
        private readonly ReleaseService releaseService;
        private readonly ProfileService profileService;
        private readonly OptiScalerSettingsViewModel settings;
        private readonly ICollectionView gamesView;
        private GameRowViewModel selectedGame;
        private string searchText = string.Empty;
        private string statusFilter = "全部";
        private bool isBusy;
        private string operationText = "就绪";
        private ReleaseInfo selectedRelease;
        private string releaseChannel;
        private Task refreshTask;
        private DateTime lastRefreshUtc = DateTime.MinValue;
        private int refreshRequestId;
        private string packageVersion;
        private ReleaseInfo selectedFsr4Release;
        private string fsr4Scope = "auto";
        private static readonly TimeSpan RefreshCacheDuration = TimeSpan.FromSeconds(3);
        public ObservableCollection<ReleaseInfo> Fsr4Releases { get; } = new ObservableCollection<ReleaseInfo>();
        public IReadOnlyList<string> Fsr4ScopeOptions { get; } = new[] { "auto", "upscaler", "all" };
        public ReleaseInfo SelectedFsr4Release { get => selectedFsr4Release; set { selectedFsr4Release = value; OnPropertyChanged(nameof(SelectedFsr4Release)); RaiseCommands(); } }
        public string Fsr4Scope { get => fsr4Scope; set { fsr4Scope = string.IsNullOrWhiteSpace(value) ? "auto" : value; OnPropertyChanged(nameof(Fsr4Scope)); } }
        public ICommand CheckFsr4ReleasesCommand { get; }
        public ICommand SwapFsr4Command { get; }
        public ICommand RestoreFsr4Command { get; }
        public IReadOnlyList<string> UpscalerOptions { get; } = new[] { "auto", "fsr21", "fsr22", "fsr31", "fsr21_12", "fsr22_12", "fsr31_12", "xess", "xess_12", "dlss" };
        public IReadOnlyList<string> FrameGenerationOptions { get; } = new[] { "auto", "true", "false" };
        public IReadOnlyList<string> FrameGenerationInputOptions { get; } = new[] { "auto", "nofg", "dlssg", "nvngxfg", "nukems", "fsrfg", "upscaler", "fsrfg30" };
        public IReadOnlyList<string> FrameGenerationOutputOptions { get; } = new[] { "auto", "nofg", "fsrfg", "xefg", "nvngxfg", "nukems", "dlssg", "dlssgwithnvngx" };
        public IReadOnlyList<string> BooleanOptions { get; } = new[] { "auto", "true", "false" };
        public IReadOnlyList<string> OutputScalingDownscalerOptions { get; } = new[] { "auto", "0", "1", "2", "3", "4", "5", "6", "7" };
        public IReadOnlyList<string> Fsr4ModelOptions { get; } = new[] { "auto", "0", "1", "2", "3", "4", "5" };
        public IReadOnlyList<string> InjectionMethods { get; } = new[] { "dxgi.dll", "winmm.dll", "d3d12.dll", "dbghelp.dll", "version.dll", "wininet.dll", "winhttp.dll", "OptiScaler.asi" };
        public IReadOnlyList<string> ReleaseChannels { get; } = new[] { "stable", "beta", "nightly" };

        public ObservableCollection<GameRowViewModel> Games { get; } = new ObservableCollection<GameRowViewModel>();
        public ObservableCollection<ReleaseInfo> Releases { get; } = new ObservableCollection<ReleaseInfo>();
        public ObservableCollection<string> StatusFilters { get; } = new ObservableCollection<string> { "全部", "已安装", "未安装", "需要处理" };
        public ObservableCollection<string> ProfileNames { get; } = new ObservableCollection<string>();
        public ICollectionView GamesView => gamesView;
        public GameRowViewModel SelectedGame
        {
            get => selectedGame;
            set
            {
                selectedGame = value;
                if (selectedGame != null && selectedGame.CanEditConfiguration)
                {
                    try { selectedGame.LoadQuickConfiguration(installationService.ReadConfiguration(selectedGame.Entry.ManagedGame)); }
                    catch { }
                }
                OnPropertyChanged(nameof(SelectedGame));
                OnPropertyChanged(nameof(HasSelectedGame));
                RaiseCommands();
            }
        }
        public ReleaseInfo SelectedRelease { get => selectedRelease; set { selectedRelease = value; OnPropertyChanged(nameof(SelectedRelease)); RaiseCommands(); } }
        public string ReleaseChannel
        {
            get => releaseChannel;
            set
            {
                var normalized = NormalizeReleaseChannel(value);
                if (string.Equals(releaseChannel, normalized, StringComparison.OrdinalIgnoreCase)) return;
                releaseChannel = normalized;
                settings.Settings.ReleaseChannel = normalized;
                settings.EndEdit();
                OnPropertyChanged(nameof(ReleaseChannel));
                LoadCachedReleases();
            }
        }
        public string SearchText { get => searchText; set { searchText = value ?? string.Empty; OnPropertyChanged(nameof(SearchText)); RefreshGamesView(); } }
        public string StatusFilter { get => statusFilter; set { statusFilter = value ?? "全部"; OnPropertyChanged(nameof(StatusFilter)); RefreshGamesView(); } }
        public bool IsBusy { get => isBusy; private set { isBusy = value; OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(ShowEmptyList)); RaiseCommands(); } }
        public int VisibleGameCount => gamesView?.Cast<object>().Count() ?? 0;
        public string GameCountText => VisibleGameCount == Games.Count ? Games.Count + " 个游戏" : VisibleGameCount + " / " + Games.Count + " 个游戏";
        public bool HasSelectedGame => SelectedGame != null;
        public bool ShowEmptyList => !IsBusy && VisibleGameCount == 0;
        public string EmptyListText => Games.Count == 0
            ? (settings.Settings.UseCustomGameList ? "自定义列表为空，点击“编辑游戏列表”添加游戏。" : "Playnite 中没有已安装的游戏。")
            : "没有符合筛选条件的游戏。";
        public string PackageFileName => string.IsNullOrWhiteSpace(PackagePath) ? "尚未选择安装包" : Path.GetFileName(PackagePath.TrimEnd('\\', '/'));
        public bool HasPackage => !string.IsNullOrWhiteSpace(PackagePath) && (File.Exists(PackagePath) || Directory.Exists(PackagePath));
        public string OperationText { get => operationText; private set { operationText = value; OnPropertyChanged(nameof(OperationText)); } }
        public string GameListModeText => settings.Settings.UseCustomGameList ? "显示自定义列表中的已安装游戏" : "显示全部已安装的 Playnite 游戏";
        public string PackagePath
        {
            get => settings.Settings.PackagePath;
            set
            {
                settings.Settings.PackagePath = value;
                packageVersion = null;
                OnPropertyChanged(nameof(PackagePath));
                OnPropertyChanged(nameof(PackageFileName));
                OnPropertyChanged(nameof(HasPackage));
                RaiseCommands();
            }
        }

        public ICommand RefreshCommand { get; }
        public ICommand ChoosePackageCommand { get; }
        public ICommand CheckReleasesCommand { get; }
        public ICommand DownloadReleaseCommand { get; }
        public ICommand InstallCommand { get; }
        public ICommand UninstallCommand { get; }
        public ICommand ConfigureCommand { get; }
        public ICommand SaveQuickConfigurationCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand EditGameListCommand { get; }

        public OptiScalerSidebarViewModel(OptiScalerPlugin plugin, PlayniteGameCatalog catalog, InstallationService installationService, ReleaseService releaseService, ProfileService profileService, OptiScalerSettingsViewModel settings)
        {
            this.plugin = plugin;
            this.catalog = catalog;
            this.installationService = installationService;
            this.releaseService = releaseService;
            this.profileService = profileService;
            this.settings = settings;
            releaseChannel = NormalizeReleaseChannel(settings.Settings.ReleaseChannel);
            if (!string.Equals(settings.Settings.ReleaseChannel, releaseChannel, StringComparison.OrdinalIgnoreCase))
                settings.Settings.ReleaseChannel = releaseChannel;
            foreach (var profile in profileService?.ListProfiles() ?? new string[0]) ProfileNames.Add(profile);
            gamesView = CollectionViewSource.GetDefaultView(Games);
            gamesView.Filter = FilterGame;
            RefreshCommand = new DelegateCommand(() => Refresh(true), () => !IsBusy);
            ChoosePackageCommand = new DelegateCommand(ChoosePackage, () => !IsBusy);
            CheckReleasesCommand = new DelegateCommand(CheckReleases, () => !IsBusy);
            DownloadReleaseCommand = new DelegateCommand(DownloadRelease, CanDownloadRelease);
            InstallCommand = new DelegateCommand(Install, CanInstall);
            UninstallCommand = new DelegateCommand(Uninstall, CanUninstall);
            ConfigureCommand = new DelegateCommand(Configure, CanConfigure);
            SaveQuickConfigurationCommand = new DelegateCommand(SaveQuickConfiguration, CanConfigure);
            OpenFolderCommand = new DelegateCommand(OpenFolder, () => SelectedGame != null && Directory.Exists(SelectedGame.InstallDirectory));
            EditGameListCommand = new DelegateCommand(EditGameList, () => !IsBusy);
            CheckFsr4ReleasesCommand = new DelegateCommand(CheckFsr4Releases, () => !IsBusy);
            SwapFsr4Command = new DelegateCommand(SwapFsr4, CanSwapFsr4);
            RestoreFsr4Command = new DelegateCommand(RestoreFsr4, CanRestoreFsr4);
            foreach (var release in releaseService.GetCachedFsr4Releases()) Fsr4Releases.Add(release);
            SelectedFsr4Release = Fsr4Releases.FirstOrDefault();
            foreach (var release in releaseService.GetCachedReleases(releaseChannel)) Releases.Add(release);
            SelectedRelease = Releases.OrderByDescending(x => x.PublishedAt).FirstOrDefault();
            if (settings.Settings.CheckForUpdatesOnOpen) CheckReleases();
        }

        public void Refresh() => Refresh(false);

        public void Refresh(bool force)
        {
            if (IsBusy) return;
            if (!force && Games.Count > 0 && refreshTask == null && DateTime.UtcNow - lastRefreshUtc < RefreshCacheDuration) return;
            if (refreshTask != null && !refreshTask.IsCompleted) return;

            var selectedId = SelectedGame?.Entry.ManagedGame.Id;
            var requestId = Interlocked.Increment(ref refreshRequestId);
            IsBusy = true;
            OperationText = "正在读取已安装的游戏...";
            refreshTask = RefreshAsync(requestId, selectedId);
        }

        private async Task RefreshAsync(int requestId, Guid? selectedId)
        {
            try
            {
                // Directory inspection is the expensive part of catalog loading. Keep it off
                // Playnite's UI thread so opening the sidebar remains responsive.
                var useCustomGameList = settings.Settings.UseCustomGameList;
                var managedGameIds = new HashSet<string>(settings.Settings.ManagedGameIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                var entries = await Task.Run(() => catalog.ReadGames(true, true)
                    .Where(x => !useCustomGameList || managedGameIds.Contains(x.ManagedGame.Id.ToString("D")))
                    .ToList());
                RunOnUiThread(() =>
                {
                    if (requestId != refreshRequestId) return;
                    Games.Clear();
                    foreach (var entry in entries) Games.Add(new GameRowViewModel(entry, settings.Settings.InjectionMethod));
                    SelectedGame = Games.FirstOrDefault(x => x.Entry.ManagedGame.Id == selectedId) ?? Games.FirstOrDefault();
                    ApplyLatestReleaseStatus();
                    RefreshGamesView();
                    lastRefreshUtc = DateTime.UtcNow;
                    OperationText = "就绪";
                });
            }
            catch (Exception ex)
            {
                RunOnUiThread(() =>
                {
                    if (requestId != refreshRequestId) return;
                    OperationText = "读取游戏失败：" + ex.Message;
                    plugin.Api.Notifications.Add("OptiScaler.Refresh", ex.Message, global::Playnite.SDK.NotificationType.Error);
                });
            }
            finally
            {
                RunOnUiThread(() =>
                {
                    if (requestId != refreshRequestId) return;
                    IsBusy = false;
                    refreshTask = null;
                });
            }
        }

        private static void RunOnUiThread(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.Invoke(action);
            else action();
        }

        public void RefreshForGame(Guid gameId)
        {
            var row = Games.FirstOrDefault(x => x.Entry.ManagedGame.Id == gameId);
            if (row == null) { Refresh(true); return; }
            row.UpdateStatus(catalog.Analyze(row.Entry.ManagedGame));
            RefreshGamesView();
        }

        // Refreshes the filtered view and the bindings derived from it.
        private void RefreshGamesView()
        {
            gamesView?.Refresh();
            OnPropertyChanged(nameof(VisibleGameCount));
            OnPropertyChanged(nameof(GameCountText));
            OnPropertyChanged(nameof(ShowEmptyList));
            OnPropertyChanged(nameof(EmptyListText));
        }

        private void RefreshProfiles()
        {
            ProfileNames.Clear();
            if (profileService == null) return;
            foreach (var profile in profileService.ListProfiles()) ProfileNames.Add(profile);
        }

        private void LoadCachedReleases()
        {
            Releases.Clear();
            foreach (var release in releaseService.GetCachedReleases(ReleaseChannel)) Releases.Add(release);
            SelectedRelease = Releases.OrderByDescending(x => x.PublishedAt).FirstOrDefault();
            ApplyLatestReleaseStatus();
        }

        private static string NormalizeReleaseChannel(string value)
        {
            if (string.Equals(value, "beta", StringComparison.OrdinalIgnoreCase)) return "beta";
            if (string.Equals(value, "nightly", StringComparison.OrdinalIgnoreCase)) return "nightly";
            return "stable";
        }

        private void ApplyLatestReleaseStatus()
        {
            var latest = Releases.OrderByDescending(x => x.PublishedAt).FirstOrDefault();
            if (latest == null) return;
            foreach (var game in Games) game.MarkUpdateAvailable(latest.TagName);
            RefreshGamesView();
        }

        private void ChoosePackage()
        {
            var path = plugin.SelectPackagePath();
            if (!string.IsNullOrWhiteSpace(path)) PackagePath = path;
        }

        private void EditGameList()
        {
            if (IsBusy) return;
            try
            {
                if (!plugin.EditGameList()) return;
                OnPropertyChanged(nameof(GameListModeText));
                Refresh(true);
            }
            catch (Exception ex)
            {
                OperationText = "编辑游戏列表失败：" + ex.Message;
                plugin.Api.Notifications.Add("OptiScaler.GameList", ex.Message, global::Playnite.SDK.NotificationType.Error);
            }
        }

        private async void CheckReleases()
        {
            if (IsBusy) return;
            IsBusy = true;
            OperationText = "正在检查 OptiScaler 更新...";
            try
            {
                var releases = await releaseService.GetReleasesAsync(ReleaseChannel, true);
                Releases.Clear();
                foreach (var release in releases) Releases.Add(release);
                SelectedRelease = Releases.OrderByDescending(x => x.PublishedAt).FirstOrDefault();
                ApplyLatestReleaseStatus();
                OperationText = Releases.Count == 0 ? "当前通道没有找到可用的安装包" : "更新信息已刷新";
            }
            catch (Exception ex)
            {
                OperationText = "检查更新失败：" + ex.Message;
                plugin.Api.Notifications.Add("OptiScaler.Release", ex.Message, global::Playnite.SDK.NotificationType.Error);
            }
            finally
            {
                IsBusy = false;
                if (Games.Count == 0) Refresh();
            }
        }

        private async void DownloadRelease()
        {
            if (!CanDownloadRelease()) return;
            var release = SelectedRelease;
            IsBusy = true;
            OperationText = "正在下载 " + release.Name + "...";
            try
            {
                var progress = new Progress<double>(value => OperationText = "正在下载 " + release.Name + "（" + Math.Round(value * 100) + "%）...");
                PackagePath = await releaseService.DownloadReleaseAsync(release, progress);
                packageVersion = string.IsNullOrWhiteSpace(release.TagName) ? release.Name : release.TagName;
                OperationText = "安装包下载完成";
            }
            catch (Exception ex)
            {
                OperationText = "下载失败：" + ex.Message;
                plugin.Api.Notifications.Add("OptiScaler.Release", ex.Message, global::Playnite.SDK.NotificationType.Error);
            }
            finally { IsBusy = false; }
        }

        private async void Install()
        {
            if (!CanInstall()) return;
            var row = SelectedGame;
            var package = PackagePath;
            var confirmed = plugin.Api.Dialogs.ShowMessage("确定要为“" + row.Name + "”安装或更新 OptiScaler 吗？", "OptiScaler", System.Windows.MessageBoxButton.YesNo) == System.Windows.MessageBoxResult.Yes;
            if (!confirmed) return;
            var version = packageVersion;
            var selectedProfile = row.ProfileName;
            string profileContents = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(selectedProfile)) profileContents = profileService.Load(selectedProfile);
            }
            catch (Exception ex)
            {
                OperationText = "读取 profile 失败：" + ex.Message;
                plugin.Api.Notifications.Add("OptiScaler.Profile", ex.Message, global::Playnite.SDK.NotificationType.Error);
                return;
            }
            await RunOperationAsync("正在为“" + row.Name + "”安装...", () => installationService.Install(row.Entry.ManagedGame, package, row.InjectionMethod, version, profileContents, selectedProfile));
        }

        private async void Uninstall()
        {
            if (!CanUninstall()) return;
            var row = SelectedGame;
            var confirmed = plugin.Api.Dialogs.ShowMessage("确定要从“" + row.Name + "”卸载 OptiScaler 吗？", "OptiScaler", System.Windows.MessageBoxButton.YesNo) == System.Windows.MessageBoxResult.Yes;
            if (!confirmed) return;
            await RunOperationAsync("正在从“" + row.Name + "”卸载...", () => installationService.Uninstall(row.Entry.ManagedGame));
        }

        private void Configure()
        {
            if (!CanConfigure()) return;
            try
            {
                plugin.EditConfiguration(SelectedGame.Entry.ManagedGame, installationService);
                RefreshProfiles();
                RefreshForGame(SelectedGame.Entry.ManagedGame.Id);
            }
            catch (Exception ex)
            {
                OperationText = ex.Message;
                plugin.Api.Notifications.Add("OptiScaler.Configuration", ex.Message, global::Playnite.SDK.NotificationType.Error);
            }
        }

        private void SaveQuickConfiguration()
        {
            if (!CanConfigure()) return;
            try
            {
                var contents = installationService.ReadConfiguration(SelectedGame.Entry.ManagedGame);
                SelectedGame.SaveQuickConfiguration(contents);
                installationService.SaveConfiguration(SelectedGame.Entry.ManagedGame, SelectedGame.QuickConfigurationText);
                OperationText = "快速配置已保存";
                RefreshForGame(SelectedGame.Entry.ManagedGame.Id);
            }
            catch (Exception ex)
            {
                OperationText = "保存配置失败：" + ex.Message;
                plugin.Api.Notifications.Add("OptiScaler.Configuration", ex.Message, global::Playnite.SDK.NotificationType.Error);
            }
        }

        private async Task RunOperationAsync(string message, Action operation)
        {
            IsBusy = true;
            OperationText = message;
            try
            {
                await Task.Run(operation);
                OperationText = "操作完成";
            }
            catch (Exception ex)
            {
                OperationText = ex.Message;
                plugin.Api.Notifications.Add("OptiScaler.Operation", ex.Message, global::Playnite.SDK.NotificationType.Error);
            }
            finally
            {
                IsBusy = false;
                Refresh(true);
            }
        }

        private async void CheckFsr4Releases()
        {
            if (IsBusy) return;
            IsBusy = true;
            OperationText = "正在获取 FSR4 版本列表...";
            try
            {
                var releases = await releaseService.GetFsr4ReleasesAsync(true);
                var previous = SelectedFsr4Release?.AssetUrl;
                Fsr4Releases.Clear();
                foreach (var release in releases) Fsr4Releases.Add(release);
                SelectedFsr4Release = Fsr4Releases.FirstOrDefault(x => x.AssetUrl == previous) ?? Fsr4Releases.FirstOrDefault();
                OperationText = Fsr4Releases.Count == 0 ? "没有找到可用的 FSR4 版本" : "FSR4 版本列表已刷新";
            }
            catch (Exception ex)
            {
                OperationText = "获取 FSR4 版本失败：" + ex.Message;
                plugin.Api.Notifications.Add("OptiScaler.Fsr4", ex.Message, global::Playnite.SDK.NotificationType.Error);
            }
            finally { IsBusy = false; }
        }

        private async void SwapFsr4()
        {
            if (!CanSwapFsr4()) return;
            var row = SelectedGame;
            var release = SelectedFsr4Release;
            var scope = Fsr4Scope;
            var message = "确定要把“" + row.Name + "”的 FSR4 DLL 替换为 " + release.Name + " 吗？\n\n原始文件会先备份，之后可以用“还原 FSR4”恢复。";
            if (plugin.Api.Dialogs.ShowMessage(message, "FSR4 Swap", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

            IsBusy = true;
            try
            {
                var progress = new Progress<double>(value => OperationText = "正在下载 " + release.Name + "（" + Math.Round(value * 100) + "%）...");
                OperationText = "正在下载 " + release.Name + "...";
                var package = await releaseService.DownloadReleaseAsync(release, progress);
                OperationText = "正在替换 FSR4 DLL...";
                var result = await Task.Run(() => installationService.SwapFsr4Dlls(row.Entry.ManagedGame, package, release.Name, scope));
                OperationText = "FSR4 已替换：" + string.Join("、", result.Files.Select(Path.GetFileName));
            }
            catch (Exception ex)
            {
                OperationText = "FSR4 替换失败：" + ex.Message;
                plugin.Api.Notifications.Add("OptiScaler.Fsr4", ex.Message, global::Playnite.SDK.NotificationType.Error);
            }
            finally
            {
                IsBusy = false;
                RefreshForGame(row.Entry.ManagedGame.Id);
            }
        }

        private async void RestoreFsr4()
        {
            if (!CanRestoreFsr4()) return;
            var row = SelectedGame;
            if (plugin.Api.Dialogs.ShowMessage("确定要把“" + row.Name + "”的 FSR4 DLL 还原为替换前的文件吗？", "FSR4 Swap", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            IsBusy = true;
            OperationText = "正在还原 FSR4 DLL...";
            try
            {
                await Task.Run(() => installationService.RestoreFsr4Dlls(row.Entry.ManagedGame));
                OperationText = "FSR4 DLL 已还原";
            }
            catch (Exception ex)
            {
                OperationText = "FSR4 还原失败：" + ex.Message;
                plugin.Api.Notifications.Add("OptiScaler.Fsr4", ex.Message, global::Playnite.SDK.NotificationType.Error);
            }
            finally
            {
                IsBusy = false;
                RefreshForGame(row.Entry.ManagedGame.Id);
            }
        }

        private bool CanSwapFsr4() => !IsBusy && SelectedGame != null && SelectedFsr4Release != null && SelectedGame.Entry.ManagedGame.IsInstalledInPlaynite && !SelectedGame.Entry.ManagedGame.IsRunning;
        private bool CanRestoreFsr4() => !IsBusy && SelectedGame != null && SelectedGame.HasFsr4Swap && !SelectedGame.Entry.ManagedGame.IsRunning;

        private void OpenFolder()
        {
            try { System.Diagnostics.Process.Start("explorer.exe", SelectedGame.InstallDirectory); } catch { }
        }

        private bool CanDownloadRelease() => !IsBusy && SelectedRelease != null;
        private bool CanInstall() => !IsBusy && SelectedGame != null && SelectedGame.Entry.ManagedGame.IsInstalledInPlaynite && !SelectedGame.Entry.ManagedGame.IsRunning && HasPackage;
        private bool CanUninstall() => !IsBusy && SelectedGame != null && SelectedGame.Status != null && SelectedGame.Status.IsInstalled && !SelectedGame.Entry.ManagedGame.IsRunning;
        private bool CanConfigure() => !IsBusy && SelectedGame != null && SelectedGame.CanEditConfiguration && !SelectedGame.Entry.ManagedGame.IsRunning;

        private bool FilterGame(object value)
        {
            var row = value as GameRowViewModel;
            if (row == null) return false;
            if (!string.IsNullOrWhiteSpace(SearchText) && row.Name.IndexOf(SearchText, StringComparison.CurrentCultureIgnoreCase) < 0) return false;
            if (StatusFilter == "已安装" && !row.IsInstalled) return false;
            if (StatusFilter == "未安装" && row.Status?.State != OptiScalerInstallState.NotInstalled) return false;
            if (StatusFilter == "需要处理" && row.Status?.State != OptiScalerInstallState.Conflict && row.Status?.State != OptiScalerInstallState.Incomplete && row.Status?.State != OptiScalerInstallState.MissingDirectory) return false;
            return true;
        }

        private void RaiseCommands()
        {
            (RefreshCommand as DelegateCommand)?.RaiseCanExecuteChanged();
            (ChoosePackageCommand as DelegateCommand)?.RaiseCanExecuteChanged();
            (CheckReleasesCommand as DelegateCommand)?.RaiseCanExecuteChanged();
            (DownloadReleaseCommand as DelegateCommand)?.RaiseCanExecuteChanged();
            (InstallCommand as DelegateCommand)?.RaiseCanExecuteChanged();
            (UninstallCommand as DelegateCommand)?.RaiseCanExecuteChanged();
            (ConfigureCommand as DelegateCommand)?.RaiseCanExecuteChanged();
            (SaveQuickConfigurationCommand as DelegateCommand)?.RaiseCanExecuteChanged();
            (OpenFolderCommand as DelegateCommand)?.RaiseCanExecuteChanged();
            (EditGameListCommand as DelegateCommand)?.RaiseCanExecuteChanged();
            (CheckFsr4ReleasesCommand as DelegateCommand)?.RaiseCanExecuteChanged();
            (SwapFsr4Command as DelegateCommand)?.RaiseCanExecuteChanged();
            (RestoreFsr4Command as DelegateCommand)?.RaiseCanExecuteChanged();
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        public void Dispose() => releaseService.Dispose();
    }
}
