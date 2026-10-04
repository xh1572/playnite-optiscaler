using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using OptiScaler.Playnite.Core.Archive;
using OptiScaler.Playnite.Core.Models;
using OptiScaler.Playnite.Core.Storage;

namespace OptiScaler.Playnite.Core.Services
{
    public sealed class InstallationService
    {
        private static readonly string[] InjectionNames =
        {
            "dxgi.dll", "winmm.dll", "d3d12.dll", "dbghelp.dll", "version.dll", "wininet.dll", "winhttp.dll", "OptiScaler.asi"
        };
        private static readonly string[] IgnoredPackageFiles =
        {
            "setup_windows.bat", "setup_linux.sh", "!! README_EXTRACT ALL FILES TO GAME FOLDER !!.txt"
        };
        private readonly BackupStore backupStore;
        private readonly string dataPath;
        private readonly object operationLock = new object();

        public InstallationService(string pluginDataPath)
        {
            dataPath = pluginDataPath;
            backupStore = new BackupStore(pluginDataPath);
        }

        public InstallationManifest Install(ManagedGame game, string packagePath, string injectionMethod, string version = null)
        {
            return Install(game, packagePath, injectionMethod, version, null, null);
        }

        public InstallationManifest Install(ManagedGame game, string packagePath, string injectionMethod, string version, string profileContents, string profileName)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            if (!game.IsInstalledInPlaynite) throw new InvalidOperationException("该游戏未安装在 Playnite 中。");
            if (game.IsRunning) throw new InvalidOperationException("请先退出游戏，再安装 OptiScaler。");
            if (!InjectionNames.Contains(injectionMethod ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("不支持当前的注入 DLL。", nameof(injectionMethod));

            var installDirectory = ResolveInstallDirectory(game);
            if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
                throw new DirectoryNotFoundException("无法确定游戏安装目录。");

            lock (operationLock)
            {
                var staging = Path.Combine(dataPath, "Staging", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                InstallationManifest manifest = null;
                try
                {
                    var extractedRoot = PackageExtractor.PreparePackage(packagePath, staging);
                    if (!Directory.Exists(extractedRoot)) throw new DirectoryNotFoundException(extractedRoot);
                    var packageRoot = PackageExtractor.FindPackageRoot(extractedRoot);
                    var mainDll = FindMainDll(packageRoot);
                    var packageFiles = Directory.GetFiles(packageRoot, "*", SearchOption.AllDirectories)
                        .Where(path => !IgnoredPackageFiles.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
                        .ToList();
                    var detectedVersion = string.IsNullOrWhiteSpace(version) ? ReadFileVersion(mainDll) : version;
                    if (string.IsNullOrWhiteSpace(detectedVersion)) detectedVersion = ReadPackageVersion(packagePath, packageRoot);

                    var previous = backupStore.LoadManifest(game.InstallDirectory);
                    if (previous == null)
                    {
                        backupStore.TryMigrateLegacyInFolder(game.InstallDirectory, installDirectory, game.Id);
                        previous = backupStore.LoadManifest(game.InstallDirectory);
                    }
                    if (previous == null)
                    {
                        backupStore.TryMigrateLegacyClient(game.InstallDirectory, installDirectory, game.Id);
                        previous = backupStore.LoadManifest(game.InstallDirectory);
                    }
                    if (previous != null && !string.Equals(previous.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase))
                        RecoverIncomplete(game.InstallDirectory);
                    if (previous != null && !string.Equals(previous.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase))
                        previous = backupStore.LoadManifest(game.InstallDirectory);
                    var isUpdate = previous != null && string.Equals(previous.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase);
                    if (!isUpdate) previous = null;
                    var previousCreatedPaths = previous == null
                        ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(previous.FilesCreated.Select(x => x.RelativePath), StringComparer.OrdinalIgnoreCase);
                    var previousTrackedPaths = previous == null
                        ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(previous.FilesCreated.Concat(previous.FilesOverwritten).Select(x => x.RelativePath), StringComparer.OrdinalIgnoreCase);
                    backupStore.DeleteUpdateFiles(game.InstallDirectory);

                    // Keep the user's current OptiScaler.ini when an existing managed install is
                    // updated. The package ships a fresh template, but replacing a hand-edited
                    // configuration would silently discard per-game settings.
                    byte[] preservedIni = null;
                    string preservedIniRelative = null;
                    if (isUpdate)
                    {
                        var existingIni = FindConfigurationPath(installDirectory);
                        if (existingIni != null)
                        {
                            preservedIni = File.ReadAllBytes(existingIni);
                            preservedIniRelative = BackupStore.NormalizeRelative(PackageExtractor.MakeRelativePath(installDirectory, existingIni));
                        }
                    }

                    manifest = new InstallationManifest
                    {
                        OperationId = Guid.NewGuid().ToString("N"),
                        OperationStatus = "in_progress",
                        GameId = game.Id.ToString("D"),
                        GameInstallDirectory = game.InstallDirectory,
                        InstalledGameDirectory = installDirectory,
                        OptiScalerVersion = detectedVersion,
                        InjectionMethod = injectionMethod,
                        AppliedProfileName = string.IsNullOrWhiteSpace(profileName) ? null : profileName,
                        InstallDateUtc = DateTime.UtcNow.ToString("O")
                    };
                    PopulateComponents(manifest, packageFiles);
                    if (isUpdate)
                    {
                        foreach (var directory in previous.InstalledDirectories)
                            if (!manifest.InstalledDirectories.Contains(directory, StringComparer.OrdinalIgnoreCase))
                                manifest.InstalledDirectories.Add(directory);
                    }
                    manifest.ExpectedFinalMarkers.Add(injectionMethod);
                    if (isUpdate) manifest.PreviousManifest = previous;
                    backupStore.SaveManifest(game.InstallDirectory, manifest);

                    // Files created by the previous version have no original-game backup, so a
                    // failed update would otherwise delete them and leave no working install.
                    if (isUpdate)
                    {
                        foreach (var relative in previousTrackedPaths)
                            if (backupStore.BackupUpdateFile(game.InstallDirectory, installDirectory, relative))
                                manifest.UpdateBackupFiles.Add(relative);
                        backupStore.SaveManifest(game.InstallDirectory, manifest);
                    }

                    var newPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var source in packageFiles)
                    {
                        var relative = PackageExtractor.MakeRelativePath(packageRoot, source);
                        if (string.Equals(relative, Path.GetFileName(mainDll), StringComparison.OrdinalIgnoreCase))
                            relative = injectionMethod;
                        relative = BackupStore.NormalizeRelative(relative);
                        if (string.Equals(relative, "OptiScaler.dll", StringComparison.OrdinalIgnoreCase)) continue;
                        newPaths.Add(relative);
                        CopyTracked(game.InstallDirectory, installDirectory, relative, source, manifest, previousCreatedPaths.Contains(relative));
                    }

                    backupStore.SaveManifest(game.InstallDirectory, manifest);

                    if (profileContents != null)
                    {
                        ApplyProfileConfiguration(installDirectory, manifest, profileContents);
                        backupStore.SaveManifest(game.InstallDirectory, manifest);
                    }
                    else if (preservedIni != null)
                    {
                        var iniPath = Path.Combine(installDirectory, preservedIniRelative);
                        if (File.Exists(iniPath))
                        {
                            File.WriteAllBytes(iniPath, preservedIni);
                            var iniRecord = manifest.FilesOverwritten.Concat(manifest.FilesCreated).LastOrDefault(x =>
                                string.Equals(BackupStore.NormalizeRelative(x.RelativePath), preservedIniRelative, StringComparison.OrdinalIgnoreCase));
                            if (iniRecord != null) iniRecord.PostInstallSha256 = BackupStore.ComputeSha256(iniPath);
                            backupStore.SaveManifest(game.InstallDirectory, manifest);
                        }
                    }

                    // Remove stale files from an earlier tracked install only when they are still
                    // byte-for-byte equal to the previous installed content.
                    if (isUpdate)
                        RemoveStalePreviousFiles(game.InstallDirectory, installDirectory, previous, newPaths, manifest);

                    manifest.OperationStatus = "committed";
                    manifest.PreviousManifest = null;
                    manifest.UpdateBackupFiles.Clear();
                    backupStore.SaveManifest(game.InstallDirectory, manifest);
                    backupStore.DeleteUpdateFiles(game.InstallDirectory);
                    return manifest;
                }
                catch (Exception ex)
                {
                    if (manifest != null)
                    {
                        // A failing rollback must not mask the error that caused it.
                        try { RollbackOperation(game.InstallDirectory, installDirectory, manifest, ex.Message); }
                        catch { }
                    }
                    throw;
                }
                finally
                {
                    TryDeleteDirectory(staging);
                }
            }
        }

        public UninstallResult Uninstall(ManagedGame game, bool keepBackupOnConflict = true)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            if (game.IsRunning) throw new InvalidOperationException("请先退出游戏，再卸载 OptiScaler。");
            lock (operationLock)
            {
                var result = new UninstallResult();
                var manifest = backupStore.LoadManifest(game.InstallDirectory);
                if (manifest == null)
                {
                    var detectedDirectory = ResolveInstallDirectory(game);
                    backupStore.TryMigrateLegacyInFolder(game.InstallDirectory, detectedDirectory, game.Id);
                    manifest = backupStore.LoadManifest(game.InstallDirectory);
                }
                if (manifest == null)
                {
                    var detectedDirectory = ResolveInstallDirectory(game);
                    backupStore.TryMigrateLegacyClient(game.InstallDirectory, detectedDirectory, game.Id);
                    manifest = backupStore.LoadManifest(game.InstallDirectory);
                }
                if (manifest == null || !string.Equals(manifest.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("没有找到该游戏已提交的 OptiScaler 安装记录。");

                var installDirectory = Directory.Exists(manifest.InstalledGameDirectory)
                    ? manifest.InstalledGameDirectory
                    : ResolveInstallDirectory(game);
                if (!Directory.Exists(installDirectory)) throw new DirectoryNotFoundException(installDirectory);

                // Records handled successfully are dropped from the manifest if the uninstall stops
                // on a conflict, so a retry only re-checks the files that are still outstanding.
                var handled = new HashSet<ManifestFileRecord>();
                foreach (var record in manifest.FilesOverwritten)
                {
                    var current = Path.Combine(installDirectory, BackupStore.NormalizeRelative(record.RelativePath));
                    if (!File.Exists(current) || BackupStore.HasSameSha256(current, record.PostInstallSha256))
                    {
                        bool restored;
                        try { restored = backupStore.RestoreFile(game.InstallDirectory, installDirectory, record.RelativePath, record.BackupRelativePath); }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            result.Conflicts.Add(record.RelativePath + " (" + ex.Message + ")");
                            continue;
                        }
                        if (restored)
                        {
                            result.RestoredFiles.Add(record.RelativePath);
                            handled.Add(record);
                        }
                        else
                            result.Conflicts.Add(record.RelativePath + " (backup missing)");
                    }
                    else result.Conflicts.Add(record.RelativePath);
                }

                foreach (var record in manifest.FilesCreated)
                {
                    var current = Path.Combine(installDirectory, BackupStore.NormalizeRelative(record.RelativePath));
                    if (!File.Exists(current))
                    {
                        handled.Add(record);
                        continue;
                    }
                    if (BackupStore.HasSameSha256(current, record.PostInstallSha256))
                    {
                        try { File.Delete(current); }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            result.Conflicts.Add(record.RelativePath + " (" + ex.Message + ")");
                            continue;
                        }
                        result.RemovedFiles.Add(record.RelativePath);
                        handled.Add(record);
                    }
                    else result.Conflicts.Add(record.RelativePath);
                }

                foreach (var directory in manifest.InstalledDirectories.OrderByDescending(x => x.Length))
                {
                    var path = Path.Combine(installDirectory, BackupStore.NormalizeRelative(directory));
                    try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
                    catch { }
                }

                if (result.Conflicts.Count == 0 || !keepBackupOnConflict)
                    backupStore.Delete(game.InstallDirectory);
                else
                {
                    manifest.FilesOverwritten.RemoveAll(handled.Contains);
                    manifest.FilesCreated.RemoveAll(handled.Contains);
                    backupStore.SaveManifest(game.InstallDirectory, manifest);
                    throw new IOException("由于文件已被修改，OptiScaler 未能完全卸载：" + string.Join("、", result.Conflicts));
                }
                return result;
            }
        }

        public string ReadConfiguration(ManagedGame game)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            var manifest = backupStore.LoadManifest(game.InstallDirectory);
            if (manifest == null || !string.Equals(manifest.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("该游戏不是由本插件管理的 OptiScaler 安装。");
            var iniPath = FindConfigurationPath(manifest.InstalledGameDirectory);
            if (iniPath == null) throw new FileNotFoundException("管理的安装中没有找到 OptiScaler.ini。");
            return File.ReadAllText(iniPath);
        }

        public void SaveConfiguration(ManagedGame game, string contents)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            if (game.IsRunning) throw new InvalidOperationException("请先退出游戏，再编辑 OptiScaler.ini。");
            lock (operationLock)
            {
                var manifest = backupStore.LoadManifest(game.InstallDirectory);
                if (manifest == null || !string.Equals(manifest.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("该游戏不是由本插件管理的 OptiScaler 安装。");
                var iniPath = FindConfigurationPath(manifest.InstalledGameDirectory);
                if (iniPath == null) throw new FileNotFoundException("管理的安装中没有找到 OptiScaler.ini。");
                var relativePath = PackageExtractor.MakeRelativePath(manifest.InstalledGameDirectory, iniPath);
                var record = manifest.FilesCreated.Concat(manifest.FilesOverwritten).FirstOrDefault(x =>
                    string.Equals(x.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
                if (record == null) throw new InvalidOperationException("安装记录没有跟踪这个 OptiScaler.ini 文件。");

                var temporary = iniPath + ".optiscaler.tmp";
                File.WriteAllText(temporary, contents ?? string.Empty, new System.Text.UTF8Encoding(false));
                if (File.Exists(iniPath)) File.Replace(temporary, iniPath, null);
                else File.Move(temporary, iniPath);
                record.PostInstallSha256 = BackupStore.ComputeSha256(iniPath);
                backupStore.SaveManifest(game.InstallDirectory, manifest);
            }
        }

        public bool RecoverIncomplete(string gameRoot)
        {
            // Startup recovery runs on a background thread; the lock keeps it from rolling back
            // the in-progress manifest of an install the user just started. Monitor is reentrant,
            // so the call from Install is still safe.
            lock (operationLock)
            {
                var manifest = backupStore.LoadManifest(gameRoot);
                if (manifest == null || string.Equals(manifest.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase)) return false;
                var dir = manifest.InstalledGameDirectory;
                if (!Directory.Exists(dir)) return false;
                RollbackOperation(gameRoot, dir, manifest, manifest.Error);
                return true;
            }
        }

        // Undoes an unfinished operation. For a failed update the previous version's files are
        // restored from the update snapshot and its committed manifest becomes current again.
        private void RollbackOperation(string gameRoot, string installDirectory, InstallationManifest manifest, string error)
        {
            Rollback(gameRoot, installDirectory, manifest);
            var previous = manifest.PreviousManifest;
            if (previous != null && string.Equals(previous.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase))
            {
                var restored = true;
                foreach (var relative in manifest.UpdateBackupFiles ?? new List<string>())
                {
                    try { restored &= backupStore.RestoreUpdateFile(gameRoot, installDirectory, relative); }
                    catch { restored = false; }
                }
                if (restored)
                {
                    backupStore.SaveManifest(gameRoot, previous);
                    backupStore.DeleteUpdateFiles(gameRoot);
                    return;
                }
            }
            manifest.OperationStatus = "rollback_required";
            manifest.Error = error;
            backupStore.SaveManifest(gameRoot, manifest);
        }

        public string ResolveInstallDirectory(ManagedGame game)
        {
            var candidates = new List<string>();
            foreach (var executable in game.ExecutablePaths ?? new List<string>())
                if (File.Exists(executable)) candidates.Add(Path.GetDirectoryName(executable));

            var binaries = FindBinariesWin64(game.InstallDirectory);
            if (binaries.Count > 0) candidates.InsertRange(0, binaries);
            candidates.Add(game.InstallDirectory);
            foreach (var candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                if (Directory.Exists(candidate)) return candidate;
            return game.InstallDirectory;
        }

        private void CopyTracked(string gameRoot, string installDirectory, string relative, string source, InstallationManifest manifest, bool wasPreviouslyCreated)
        {
            var destination = Path.Combine(installDirectory, BackupStore.NormalizeRelative(relative));
            var existed = File.Exists(destination);
            var preHash = existed ? BackupStore.ComputeSha256(destination) : null;
            if (existed && !wasPreviouslyCreated && !File.Exists(Path.Combine(backupStore.GetFilesDirectory(gameRoot), BackupStore.NormalizeRelative(relative))))
            {
                backupStore.BackupFile(gameRoot, installDirectory, relative);
            }

            EnsureParentDirectory(installDirectory, relative, manifest);
            var postHash = CopyFileWithHash(source, destination);
            var record = new ManifestFileRecord
            {
                RelativePath = relative,
                BackupRelativePath = relative,
                ExistedBefore = existed && !wasPreviouslyCreated,
                PreInstallSha256 = existed && !wasPreviouslyCreated ? preHash : null,
                PostInstallSha256 = postHash
            };
            if (existed && !wasPreviouslyCreated) manifest.FilesOverwritten.Add(record);
            else manifest.FilesCreated.Add(record);
            manifest.InstalledFiles.Add(relative);
        }

        private static void PopulateComponents(InstallationManifest manifest, IEnumerable<string> packageFiles)
        {
            var names = new HashSet<string>(packageFiles.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            if (names.Contains("OptiScaler.dll")) manifest.Components.Add("OptiScaler");
            if (names.Contains("fakenvapi.dll")) { manifest.Components.Add("Fakenvapi"); manifest.IncludesFakenvapi = true; }
            if (names.Contains("dlssg_to_fsr3_amd_is_better.dll") || names.Contains("libxess_fg.dll")) { manifest.Components.Add("Frame Generation"); manifest.IncludesNukemFG = true; }
            if (packageFiles.Any(x => x.IndexOf("OptiPatcher", StringComparison.OrdinalIgnoreCase) >= 0)) { manifest.Components.Add("OptiPatcher"); manifest.IncludesOptiPatcher = true; }
            foreach (var directory in packageFiles.Select(Path.GetDirectoryName).Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                if (directory.IndexOf("D3D12_Optiscaler", StringComparison.OrdinalIgnoreCase) >= 0 && !manifest.Components.Contains("D3D12 支持")) manifest.Components.Add("D3D12 支持");
            }
        }

        private static string CopyFileWithHash(string source, string destination)
        {
            using (var sha = SHA256.Create())
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
            using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 1024];
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, count);
                    sha.TransformBlock(buffer, 0, count, buffer, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", string.Empty).ToLowerInvariant();
            }
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

        private static string ReadPackageVersion(string packagePath, string packageRoot)
        {
            var names = new[]
            {
                Path.GetFileNameWithoutExtension(packagePath),
                Path.GetFileName(packageRoot)
            };
            foreach (var name in names.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                var match = Regex.Match(name, @"(?<![0-9])v?([0-9]+\.[0-9]+(?:\.[0-9]+)?(?:[-._][A-Za-z0-9]+)*)", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value;
            }
            return null;
        }

        private static void ApplyProfileConfiguration(string installDirectory, InstallationManifest manifest, string contents)
        {
            var iniPath = FindConfigurationPath(installDirectory) ?? Path.Combine(installDirectory, "OptiScaler.ini");
            EnsureParentDirectoryForProfile(installDirectory, iniPath, manifest);
            var existing = manifest.FilesCreated.Concat(manifest.FilesOverwritten).LastOrDefault(x =>
                string.Equals(x.RelativePath, PackageExtractor.MakeRelativePath(installDirectory, iniPath), StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new ManifestFileRecord
                {
                    RelativePath = PackageExtractor.MakeRelativePath(installDirectory, iniPath),
                    BackupRelativePath = PackageExtractor.MakeRelativePath(installDirectory, iniPath),
                    ExistedBefore = File.Exists(iniPath),
                    PreInstallSha256 = File.Exists(iniPath) ? BackupStore.ComputeSha256(iniPath) : null
                };
                if (existing.ExistedBefore) manifest.FilesOverwritten.Add(existing);
                else manifest.FilesCreated.Add(existing);
                manifest.InstalledFiles.Add(existing.RelativePath);
            }
            var temporary = iniPath + ".profile.tmp";
            File.WriteAllText(temporary, contents ?? string.Empty, new System.Text.UTF8Encoding(false));
            if (File.Exists(iniPath)) File.Replace(temporary, iniPath, null);
            else File.Move(temporary, iniPath);
            existing.PostInstallSha256 = BackupStore.ComputeSha256(iniPath);
        }

        private static void EnsureParentDirectoryForProfile(string installDirectory, string iniPath, InstallationManifest manifest)
        {
            var directory = Path.GetDirectoryName(iniPath);
            if (string.IsNullOrWhiteSpace(directory) || string.Equals(directory.TrimEnd(Path.DirectorySeparatorChar), installDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return;
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                var relative = PackageExtractor.MakeRelativePath(installDirectory, directory);
                if (!manifest.InstalledDirectories.Contains(relative, StringComparer.OrdinalIgnoreCase)) manifest.InstalledDirectories.Add(relative);
            }
        }

        private void RemoveStalePreviousFiles(string gameRoot, string installDirectory, InstallationManifest previous, HashSet<string> newPaths, InstallationManifest current)
        {
            foreach (var record in previous.FilesCreated)
            {
                if (newPaths.Contains(record.RelativePath)) continue;
                var path = Path.Combine(installDirectory, BackupStore.NormalizeRelative(record.RelativePath));
                if (BackupStore.HasSameSha256(path, record.PostInstallSha256))
                {
                    File.Delete(path);
                }
            }
            foreach (var record in previous.FilesOverwritten)
            {
                if (newPaths.Contains(record.RelativePath)) continue;
                var path = Path.Combine(installDirectory, BackupStore.NormalizeRelative(record.RelativePath));
                if (BackupStore.HasSameSha256(path, record.PostInstallSha256))
                {
                    backupStore.RestoreFile(gameRoot, installDirectory, record.RelativePath, record.BackupRelativePath);
                }
            }
        }

        private void EnsureParentDirectory(string installDirectory, string relative, InstallationManifest manifest)
        {
            var directory = Path.GetDirectoryName(BackupStore.NormalizeRelative(relative));
            if (string.IsNullOrEmpty(directory)) return;
            var current = installDirectory;
            foreach (var part in directory.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (!Directory.Exists(current))
                {
                    Directory.CreateDirectory(current);
                    var rel = PackageExtractor.MakeRelativePath(installDirectory, current);
                    if (!manifest.InstalledDirectories.Contains(rel, StringComparer.OrdinalIgnoreCase)) manifest.InstalledDirectories.Add(rel);
                }
            }
        }

        private void Rollback(string gameRoot, string installDirectory, InstallationManifest manifest)
        {
            foreach (var record in manifest.FilesCreated.AsEnumerable().Reverse())
            {
                var path = Path.Combine(installDirectory, BackupStore.NormalizeRelative(record.RelativePath));
                if (BackupStore.HasSameSha256(path, record.PostInstallSha256)) TryDeleteFile(path);
            }
            foreach (var record in manifest.FilesOverwritten.AsEnumerable().Reverse())
            {
                var path = Path.Combine(installDirectory, BackupStore.NormalizeRelative(record.RelativePath));
                if (!File.Exists(path) || BackupStore.HasSameSha256(path, record.PostInstallSha256))
                    backupStore.RestoreFile(gameRoot, installDirectory, record.RelativePath, record.BackupRelativePath);
            }
            foreach (var directory in manifest.InstalledDirectories.OrderByDescending(x => x.Length))
            {
                var path = Path.Combine(installDirectory, BackupStore.NormalizeRelative(directory));
                try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); } catch { }
            }
        }

        private static string FindMainDll(string packageRoot)
        {
            var opti = Directory.GetFiles(packageRoot, "OptiScaler.dll", SearchOption.AllDirectories).FirstOrDefault();
            return opti ?? Directory.GetFiles(packageRoot, "nvngx.dll", SearchOption.AllDirectories).FirstOrDefault();
        }

        private static string FindConfigurationPath(string installDirectory)
        {
            var rootIni = Path.Combine(installDirectory, "OptiScaler.ini");
            if (File.Exists(rootIni)) return rootIni;
            var nestedIni = Path.Combine(installDirectory, "OptiScaler", "OptiScaler.ini");
            return File.Exists(nestedIni) ? nestedIni : null;
        }

        private static List<string> FindBinariesWin64(string root)
        {
            var result = new List<string>();
            if (!Directory.Exists(root)) return result;
            FindBinariesWin64(root, result, 0);
            return result;
        }

        private static void FindBinariesWin64(string directory, List<string> result, int depth)
        {
            if (depth > 5) return;
            try
            {
                if (string.Equals(Path.GetFileName(directory), "Win64", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), "Binaries", StringComparison.OrdinalIgnoreCase))
                    result.Add(directory);
                foreach (var child in Directory.GetDirectories(directory)) FindBinariesWin64(child, result, depth + 1);
            }
            catch { }
        }

        private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
    }
}
