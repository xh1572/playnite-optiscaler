using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace OptiScaler.Playnite.Core.Services
{
    public sealed class Fsr4SwapTarget
    {
        public string Key { get; set; }
        public string SourcePath { get; set; }
        public string TargetPath { get; set; }
    }

    /// <summary>
    /// Known FSR4 DLL names shipped by the OptiScaler Extras packages, following OptiScaler
    /// Client's Fsr4Int8DllHelper. AMD renamed the upscaler between releases
    /// (amd_fidelityfx_upscaler_dx12.dll -> amdxcffx64.dll); FidelityFX SDK 2.0+ packages also
    /// split the other effects into separate DLLs.
    /// </summary>
    public static class Fsr4DllCatalog
    {
        public const string LegacyUpscalerFileName = "amd_fidelityfx_upscaler_dx12.dll";
        public const string CurrentUpscalerFileName = "amdxcffx64.dll";
        public static readonly IReadOnlyList<string> LogicalKeys = new[] { "Upscaler", "FrameGeneration", "Loader", "Denoiser", "RadianceCache" };

        private static readonly IReadOnlyDictionary<string, string> EffectFileNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "FrameGeneration", "amd_fidelityfx_framegeneration_dx12.dll" },
            { "Loader", "amd_fidelityfx_loader_dx12.dll" },
            { "Denoiser", "amd_fidelityfx_denoiser_dx12.dll" },
            { "RadianceCache", "amd_fidelityfx_radiancecache_dx12.dll" }
        };

        // Words used to recognize a renamed copy through its PE FileDescription.
        private static readonly IReadOnlyDictionary<string, string> DescriptionWords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Upscaler", "Upscaler" },
            { "FrameGeneration", "Frame Generation" },
            { "Loader", "Loader" },
            { "Denoiser", "Denoiser" },
            { "RadianceCache", "Radiance Cache" }
        };

        public static string GetLogicalKey(string fileName)
        {
            if (string.Equals(fileName, LegacyUpscalerFileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, CurrentUpscalerFileName, StringComparison.OrdinalIgnoreCase))
                return "Upscaler";
            return EffectFileNames.FirstOrDefault(x => string.Equals(x.Value, fileName, StringComparison.OrdinalIgnoreCase)).Key;
        }

        /// <summary>Maps each recognized logical file in an extracted package to its path (first match wins).</summary>
        public static Dictionary<string, string> FindPackageFiles(string packageRoot)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.GetFiles(packageRoot, "*.dll", SearchOption.AllDirectories).OrderBy(x => x.Length))
            {
                var key = GetLogicalKey(Path.GetFileName(path));
                if (key != null && !result.ContainsKey(key)) result.Add(key, path);
            }
            return result;
        }

        /// <summary>OptiScaler's nested layout loads FSR DLLs from "&lt;game&gt;\OptiScaler\" when that folder exists.</summary>
        public static string ResolveTargetRoot(string installDirectory)
        {
            var nested = Path.Combine(installDirectory, "OptiScaler");
            return Directory.Exists(nested) ? nested : installDirectory;
        }

        /// <summary>
        /// Decides which game files the selected package files replace. An existing upscaler keeps
        /// its current name (either known name, or a renamed copy found through PE metadata) so a
        /// game hardcoded to one filename keeps loading it; otherwise the package's own name is used.
        /// </summary>
        /// <param name="scope">
        /// "upscaler": only the upscaler; "all": every recognized file in the package;
        /// "auto": the upscaler plus any other effect DLL the game already ships.
        /// </param>
        public static List<Fsr4SwapTarget> BuildTargets(string installDirectory, IReadOnlyDictionary<string, string> packageFiles, string scope)
        {
            var targetRoot = ResolveTargetRoot(installDirectory);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var targets = new List<Fsr4SwapTarget>();
            foreach (var key in LogicalKeys)
            {
                if (!packageFiles.TryGetValue(key, out var source)) continue;
                var existing = key == "Upscaler"
                    ? FindExisting(targetRoot, LegacyUpscalerFileName, key, used) ?? FindExisting(targetRoot, CurrentUpscalerFileName, key, used)
                    : FindExisting(targetRoot, EffectFileNames[key], key, used);
                if (key != "Upscaler")
                {
                    if (string.Equals(scope, "upscaler", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase) && existing == null) continue;
                }
                var targetName = existing != null ? Path.GetFileName(existing) : Path.GetFileName(source);
                AddTarget(targets, used, key, source, Path.Combine(targetRoot, targetName));

                // A copy left next to the executable would be loaded before the nested one.
                if (!string.Equals(targetRoot, installDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    var shadow = Path.Combine(installDirectory, targetName);
                    if (File.Exists(shadow)) AddTarget(targets, used, key, source, shadow);
                }
            }
            return targets;
        }

        private static void AddTarget(List<Fsr4SwapTarget> targets, HashSet<string> used, string key, string source, string target)
        {
            if (used.Add(target)) targets.Add(new Fsr4SwapTarget { Key = key, SourcePath = source, TargetPath = target });
        }

        private static string FindExisting(string directory, string standardName, string key, HashSet<string> used)
        {
            var standard = Path.Combine(directory, standardName);
            if (File.Exists(standard)) return standard;
            try
            {
                var files = Directory.GetFiles(directory, "*fidelityfx*.dll").Concat(Directory.GetFiles(directory, "*amdxc*.dll"))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    if (used.Contains(file)) continue;
                    // A file that already carries another known name belongs to that effect.
                    var ownKey = GetLogicalKey(Path.GetFileName(file));
                    if (ownKey != null && !string.Equals(ownKey, key, StringComparison.OrdinalIgnoreCase)) continue;
                    var info = FileVersionInfo.GetVersionInfo(file);
                    if (string.Equals(info.OriginalFilename, standardName, StringComparison.OrdinalIgnoreCase)) return file;
                    if (!string.IsNullOrEmpty(info.FileDescription) &&
                        info.FileDescription.IndexOf(DescriptionWords[key], StringComparison.OrdinalIgnoreCase) >= 0)
                        return file;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }
    }
}
