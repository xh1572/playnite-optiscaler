using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using OptiScaler.Playnite.Core.Models;
using OptiScaler.Playnite.Core.Storage;

namespace OptiScaler.Playnite.Core.Services
{
    public sealed class OptiScalerAnalyzer
    {
        private static readonly string[] ProxyNames =
        {
            "dxgi.dll", "winmm.dll", "d3d12.dll", "dbghelp.dll", "version.dll", "wininet.dll", "winhttp.dll", "OptiScaler.asi"
        };
        private readonly BackupStore backupStore;

        public OptiScalerAnalyzer(string pluginDataPath)
        {
            backupStore = new BackupStore(pluginDataPath);
        }

        public OptiScalerStatus Analyze(ManagedGame game)
        {
            var status = new OptiScalerStatus { CheckedAtUtc = DateTime.UtcNow, State = OptiScalerInstallState.Unknown };
            if (game == null || string.IsNullOrWhiteSpace(game.InstallDirectory) || !Directory.Exists(game.InstallDirectory))
            {
                status.State = OptiScalerInstallState.MissingDirectory;
                status.Message = "Playnite 没有可访问的游戏安装目录。";
                return status;
            }

            var managedManifest = backupStore.LoadManifest(game.InstallDirectory);
            if (managedManifest == null)
            {
                foreach (var candidate in CandidateDirectories(game))
                {
                    if (!HasCandidateMarker(candidate)) continue;
                    if (backupStore.TryMigrateLegacyInFolder(game.InstallDirectory, candidate, game.Id))
                    {
                        managedManifest = backupStore.LoadManifest(game.InstallDirectory);
                        break;
                    }
                    if (!backupStore.TryMigrateLegacyClient(game.InstallDirectory, candidate, game.Id)) continue;
                    managedManifest = backupStore.LoadManifest(game.InstallDirectory);
                    break;
                }
            }
            if (managedManifest != null)
            {
                if (!string.Equals(managedManifest.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase))
                {
                    status.State = OptiScalerInstallState.Incomplete;
                    status.Message = "OptiScaler 上次操作未完成，需要恢复。";
                    status.Manifest = managedManifest;
                    status.InstalledDirectory = managedManifest.InstalledGameDirectory;
                    return status;
                }

                status.Manifest = managedManifest;
                status.Version = managedManifest.OptiScalerVersion;
                status.InjectionMethod = managedManifest.InjectionMethod;
                status.InstalledDirectory = managedManifest.InstalledGameDirectory;
                if (!managedManifest.IncludesOptiScaler)
                {
                    // Swap-only: no OptiScaler is managed, only FSR4 DLLs.
                    status.State = OptiScalerInstallState.NotInstalled;
                    status.Version = null;
                    status.Message = "未安装 OptiScaler；FSR4 DLL 已替换为 " + (managedManifest.Fsr4SwapVersion ?? "Extras 版本") + "。";
                    if (Directory.Exists(status.InstalledDirectory)) PopulateDetectedComponents(status, managedManifest, status.InstalledDirectory);
                    return status;
                }
                if (Directory.Exists(status.InstalledDirectory) && HasAnyMarker(status.InstalledDirectory, managedManifest))
                {
                    status.ConfigurationPath = FindConfigurationPath(status.InstalledDirectory);
                    PopulateDetectedComponents(status, managedManifest, status.InstalledDirectory);
                    if (string.IsNullOrWhiteSpace(status.Version)) status.Version = ReadDetectedVersion(status.InstalledDirectory, status.InjectionMethod);
                    status.State = OptiScalerInstallState.Installed;
                    status.Message = "由本插件管理。";
                    return status;
                }
                status.State = OptiScalerInstallState.Incomplete;
                status.Message = "安装记录存在，但安装文件缺失。";
                return status;
            }

            var detected = ScanForExistingInstallation(game);
            if (detected != null) return detected;
            status.State = OptiScalerInstallState.NotInstalled;
            status.Message = "未检测到 OptiScaler。";
            return status;
        }

        private OptiScalerStatus ScanForExistingInstallation(ManagedGame game)
        {
            foreach (var directory in CandidateDirectories(game))
            {
                var files = SafeFiles(directory);
                var ini = files.FirstOrDefault(x => string.Equals(Path.GetFileName(x), "OptiScaler.ini", StringComparison.OrdinalIgnoreCase));
                var dll = files.FirstOrDefault(x => string.Equals(Path.GetFileName(x), "OptiScaler.dll", StringComparison.OrdinalIgnoreCase));
                var oldManifest = files.FirstOrDefault(x => string.Equals(Path.GetFileName(x), "optiscaler_manifest.json", StringComparison.OrdinalIgnoreCase));
                var proxy = files.FirstOrDefault(IsOptiScalerProxy);
                if (ini == null && dll == null && oldManifest == null && proxy == null) continue;

                var result = new OptiScalerStatus
                {
                    State = OptiScalerInstallState.Installed,
                    InstalledDirectory = directory,
                    InjectionMethod = proxy == null ? string.Empty : Path.GetFileName(proxy),
                    Message = "已在游戏目录中检测到，但本插件没有记录安装归属。"
                };
                if (oldManifest != null) ReadLegacyManifest(oldManifest, result);
                if (string.IsNullOrWhiteSpace(result.Version)) result.Version = ReadVersionFromLog(directory);
                if (string.IsNullOrWhiteSpace(result.Version) && proxy != null) result.Version = ReadFileVersion(proxy);
                result.ConfigurationPath = ini;
                PopulateDetectedComponents(result, result.Manifest, directory);
                if (oldManifest != null && result.Manifest != null && !string.Equals(result.Manifest.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase))
                    result.State = OptiScalerInstallState.Incomplete;
                return result;
            }
            return null;
        }

        private static IEnumerable<string> CandidateDirectories(ManagedGame game)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<string>();
            Action<string> add = path =>
            {
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
                var full = Path.GetFullPath(path);
                if (seen.Add(full)) candidates.Add(full);
            };
            add(game.InstallDirectory);
            foreach (var path in game.ExecutablePaths ?? new List<string>())
                if (File.Exists(path)) add(Path.GetDirectoryName(path));
            foreach (var path in FindBinariesWin64(game.InstallDirectory)) add(path);

            // Existing OptiScaler installs are normally next to the executable. A shallow walk
            // catches UE and launcher layouts without scanning an entire drive.
            foreach (var directory in ShallowDirectories(game.InstallDirectory, 4, 250)) add(directory);
            return candidates;
        }

        private static IEnumerable<string> ShallowDirectories(string root, int maxDepth, int maxEntries)
        {
            var queue = new Queue<Tuple<string, int>>();
            queue.Enqueue(Tuple.Create(root, 0));
            var count = 0;
            while (queue.Count > 0 && count++ < maxEntries)
            {
                var item = queue.Dequeue();
                yield return item.Item1;
                if (item.Item2 >= maxDepth) continue;
                string[] children;
                try { children = Directory.GetDirectories(item.Item1); } catch { continue; }
                foreach (var child in children)
                {
                    var name = Path.GetFileName(child);
                    if (name.Equals("Engine", StringComparison.OrdinalIgnoreCase) || name.Equals("Intermediate", StringComparison.OrdinalIgnoreCase)) continue;
                    queue.Enqueue(Tuple.Create(child, item.Item2 + 1));
                }
            }
        }

        private static string[] SafeFiles(string directory)
        {
            try { return Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly); }
            catch { return new string[0]; }
        }

        private static bool HasCandidateMarker(string directory)
        {
            return SafeFiles(directory).Any(path =>
                string.Equals(Path.GetFileName(path), "OptiScaler.ini", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(path), "OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(path), "optiscaler_manifest.json", StringComparison.OrdinalIgnoreCase) ||
                // A bare proxy name (dxgi.dll, version.dll, ...) is common in unrelated games, so it
                // only counts when the file identifies itself as OptiScaler. This keeps the costly
                // legacy-migration scans off the background refresh path for unmanaged games.
                IsOptiScalerProxy(path));
        }

        private static bool IsOptiScalerProxy(string path)
        {
            if (!ProxyNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)) return false;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                return string.Equals(info.OriginalFilename, "OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ||
                       info.FileDescription.IndexOf("OptiScaler", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       info.ProductName.IndexOf("OptiScaler", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private static bool HasAnyMarker(string directory, InstallationManifest manifest)
        {
            if (manifest.ExpectedFinalMarkers.Count == 0) return File.Exists(Path.Combine(directory, "OptiScaler.ini"));
            return manifest.ExpectedFinalMarkers.Any(marker => File.Exists(Path.Combine(directory, BackupStore.NormalizeRelative(marker))));
        }

        private static void PopulateDetectedComponents(OptiScalerStatus status, InstallationManifest manifest, string directory)
        {
            var components = status.Components ?? new List<string>();
            foreach (var component in manifest?.Components ?? Enumerable.Empty<string>())
                if (!components.Contains(component, StringComparer.OrdinalIgnoreCase)) components.Add(component);

            var files = SafeFiles(directory);
            if (files.Any(IsOptiScalerProxy) && !components.Contains("OptiScaler", StringComparer.OrdinalIgnoreCase)) components.Add("OptiScaler");
            if (files.Any(x => string.Equals(Path.GetFileName(x), "OptiScaler.ini", StringComparison.OrdinalIgnoreCase)) && !components.Contains("OptiScaler.ini", StringComparer.OrdinalIgnoreCase)) components.Add("OptiScaler.ini");
            if (files.Any(x => string.Equals(Path.GetFileName(x), "fakenvapi.dll", StringComparison.OrdinalIgnoreCase)) && !components.Contains("Fakenvapi", StringComparer.OrdinalIgnoreCase)) components.Add("Fakenvapi");
            if (files.Any(x => Path.GetFileName(x).IndexOf("dlssg_to_fsr3", StringComparison.OrdinalIgnoreCase) >= 0 || Path.GetFileName(x).Equals("libxess_fg.dll", StringComparison.OrdinalIgnoreCase)) && !components.Contains("Frame Generation", StringComparer.OrdinalIgnoreCase)) components.Add("Frame Generation");
            if (files.Any(x => Path.GetFileName(x).IndexOf("fidelityfx", StringComparison.OrdinalIgnoreCase) >= 0) && !components.Contains("AMD FSR", StringComparer.OrdinalIgnoreCase)) components.Add("AMD FSR");
            if (files.Any(x => Path.GetFileName(x).IndexOf("xess", StringComparison.OrdinalIgnoreCase) >= 0) && !components.Contains("Intel XeSS", StringComparer.OrdinalIgnoreCase)) components.Add("Intel XeSS");
            if (files.Any(x => Path.GetFileName(x).IndexOf("nvngx", StringComparison.OrdinalIgnoreCase) >= 0 || Path.GetFileName(x).IndexOf("dlss", StringComparison.OrdinalIgnoreCase) >= 0) && !components.Contains("NVIDIA DLSS", StringComparer.OrdinalIgnoreCase)) components.Add("NVIDIA DLSS");
            var plugins = Path.Combine(directory, "plugins");
            if (Directory.Exists(plugins) && Directory.GetFiles(plugins, "*OptiPatcher*", SearchOption.TopDirectoryOnly).Length > 0 && !components.Contains("OptiPatcher", StringComparer.OrdinalIgnoreCase)) components.Add("OptiPatcher");
            status.Components = components;
        }

        private static string FindConfigurationPath(string directory)
        {
            var root = Path.Combine(directory, "OptiScaler.ini");
            if (File.Exists(root)) return root;
            var nested = Path.Combine(directory, "OptiScaler", "OptiScaler.ini");
            return File.Exists(nested) ? nested : null;
        }

        private static string ReadDetectedVersion(string directory, string injectionMethod)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(injectionMethod)) candidates.Add(Path.Combine(directory, injectionMethod));
            candidates.AddRange(ProxyNames.Select(x => Path.Combine(directory, x)));
            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(candidate)) continue;
                var version = ReadFileVersion(candidate);
                if (!string.IsNullOrWhiteSpace(version)) return version;
            }
            return ReadVersionFromLog(directory);
        }

        private static string ReadVersionFromLog(string directory)
        {
            var log = Path.Combine(directory, "OptiScaler.log");
            if (!File.Exists(log)) return null;
            try
            {
                using (var reader = new StreamReader(log))
                {
                    var text = reader.ReadToEnd();
                    var match = Regex.Match(text, @"OptiScaler\s*(?:v|version)?\s*([0-9]+\.[0-9]+(?:[-._A-Za-z0-9]*)?)", RegexOptions.IgnoreCase);
                    return match.Success ? match.Groups[1].Value : null;
                }
            }
            catch { return null; }
        }

        private static string ReadFileVersion(string path)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(info.ProductVersion) && info.ProductVersion != "0.0.0.0") return info.ProductVersion;
                if (!string.IsNullOrWhiteSpace(info.FileVersion) && info.FileVersion != "0.0.0.0") return info.FileVersion;
            }
            catch { }
            return null;
        }

        private static void ReadLegacyManifest(string path, OptiScalerStatus status)
        {
            try
            {
                var json = JObject.Parse(File.ReadAllText(path));
                status.Version = (string)(json["OptiscalerVersion"] ?? json["OptiScalerVersion"]);
                status.InjectionMethod = (string)json["InjectionMethod"] ?? status.InjectionMethod;
                status.InstalledDirectory = (string)json["InstalledGameDirectory"] ?? status.InstalledDirectory;
                status.Manifest = new InstallationManifest
                {
                    OperationStatus = (string)json["OperationStatus"] ?? "committed",
                    OptiScalerVersion = status.Version,
                    InjectionMethod = status.InjectionMethod,
                    InstalledGameDirectory = status.InstalledDirectory
                };
            }
            catch { }
        }

        private static IEnumerable<string> FindBinariesWin64(string root)
        {
            var directories = new List<string>();
            foreach (var directory in ShallowDirectories(root, 5, 250))
            {
                if (string.Equals(Path.GetFileName(directory), "Win64", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), "Binaries", StringComparison.OrdinalIgnoreCase))
                    directories.Add(directory);
            }
            // "Engine\Binaries\Win64" only holds engine helpers, so the game's own copy is checked first.
            return directories.OrderBy(IsEngineBinaries);
        }

        private static bool IsEngineBinaries(string directory)
        {
            var parent = Path.GetDirectoryName(Path.GetDirectoryName(directory));
            return parent != null && string.Equals(Path.GetFileName(parent), "Engine", StringComparison.OrdinalIgnoreCase);
        }
    }
}
