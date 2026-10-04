using System;
using System.Collections.Generic;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace OptiScaler.Playnite
{
    public class OptiScalerSettings : ObservableObject
    {
        private string packagePath = string.Empty;
        private string injectionMethod = "dxgi.dll";
        private bool showOnlyInstalled = true;
        private bool checkForUpdatesOnOpen = false;
        private string releaseChannel = "stable";
        private bool useCustomGameList;
        private List<string> managedGameIds = new List<string>();

        public string PackagePath { get => packagePath; set => SetValue(ref packagePath, value); }
        public string InjectionMethod { get => injectionMethod; set => SetValue(ref injectionMethod, value); }
        public bool ShowOnlyInstalled { get => showOnlyInstalled; set => SetValue(ref showOnlyInstalled, value); }
        public bool CheckForUpdatesOnOpen { get => checkForUpdatesOnOpen; set => SetValue(ref checkForUpdatesOnOpen, value); }
        public string ReleaseChannel { get => releaseChannel; set => SetValue(ref releaseChannel, value); }
        public bool UseCustomGameList { get => useCustomGameList; set => SetValue(ref useCustomGameList, value); }
        public List<string> ManagedGameIds
        {
            get => managedGameIds;
            set => SetValue(ref managedGameIds, value ?? new List<string>());
        }
    }

    public class OptiScalerSettingsViewModel : ObservableObject, ISettings
    {
        private readonly OptiScalerPlugin plugin;
        private OptiScalerSettings editingClone;
        private OptiScalerSettings settings;

        public OptiScalerSettings Settings
        {
            get => settings;
            set
            {
                settings = value;
                OnPropertyChanged();
            }
        }

        public OptiScalerSettingsViewModel(OptiScalerPlugin plugin)
        {
            this.plugin = plugin;
            Settings = plugin.LoadPluginSettings<OptiScalerSettings>() ?? new OptiScalerSettings();
        }

        public void BeginEdit() => editingClone = Serialization.GetClone(Settings);
        public void CancelEdit() => Settings = editingClone ?? Settings;
        public void EndEdit() => plugin.SavePluginSettings(Settings);
        public void ChoosePackage()
        {
            var path = plugin.SelectPackagePath();
            if (!string.IsNullOrWhiteSpace(path)) Settings.PackagePath = path;
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "dxgi.dll", "winmm.dll", "d3d12.dll", "dbghelp.dll", "version.dll", "wininet.dll", "winhttp.dll", "OptiScaler.asi"
            };
            if (!allowed.Contains(Settings.InjectionMethod ?? string.Empty))
                errors.Add("不支持当前的注入 DLL。");
            return errors.Count == 0;
        }
    }
}
