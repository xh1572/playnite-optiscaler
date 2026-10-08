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
                Tuple<string, string, string> pendingSwapReapply = null;
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

                    // An FSR4 swap is undone before installing and re-applied afterwards, so the
                    // update sees the files it owns and the swapped DLLs end up on top again.
                    string reapplySwapPackage = null, reapplySwapVersion = null, reapplySwapScope = null;
                    if (previous != null && previous.IncludesFsr4Swap && string.Equals(previous.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase))
                    {
                        reapplySwapPackage = previous.Fsr4SwapPackagePath;
                        reapplySwapVersion = previous.Fsr4SwapVersion;
                        reapplySwapScope = previous.Fsr4SwapScope;
                    }

                    // A recorded install can sit in a different folder than the one resolved now (for
                    // example an Unreal game whose engine folder used to win over the project folder).
                    // Its relative paths would otherwise be applied to the new folder, so the old
                    // install is undone in place first and this becomes a fresh install.
                    if (previous != null && string.Equals(previous.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(BackupStore.NormalizeDirectory(previous.InstalledGameDirectory), BackupStore.NormalizeDirectory(installDirectory), StringComparison.OrdinalIgnoreCase))
                    {
                        if (Directory.Exists(previous.InstalledGameDirectory))
                        {
                            Uninstall(game);
                        }
                        else
                        {
                            backupStore.Delete(game.InstallDirectory);
                        }
                        previous = null;
                    }
                    else if (reapplySwapPackage != null)
                    {
                        RestoreFsr4Dlls(game);
                        previous = backupStore.LoadManifest(game.InstallDirectory);
                    }
                    pendingSwapReapply = reapplySwapPackage == null ? null : Tuple.Create(reapplySwapPackage, reapplySwapVersion, reapplySwapScope);
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

                    if (pendingSwapReapply != null && (File.Exists(pendingSwapReapply.Item1) || Directory.Exists(pendingSwapReapply.Item1)))
                    {
                        // The OptiScaler install is already committed; a failed re-swap leaves it
                        // working without FSR4 Swap instead of failing the whole update.
                        try { SwapFsr4Dlls(game, pendingSwapReapply.Item1, pendingSwapReapply.Item2, pendingSwapReapply.Item3); }
                        catch { }
                        manifest = backupStore.LoadManifest(game.InstallDirectory) ?? manifest;
                    }
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

        private const string Fsr4PreservedScope = "fsr4-files";

        /// <summary>
        /// Replaces or adds FSR4 DLLs from an OptiScaler Extras package without touching the rest of
        /// the install. Joins an existing committed manifest (OptiScaler installed, or an earlier
        /// swap) or creates a swap-only one. Game originals are backed up in "files"; a file the
        /// manifest already owned (e.g. OptiScaler's own FSR DLL) is preserved in "fsr4-files" so
        /// RestoreFsr4Dlls returns to that copy. A failure rolls back only this swap.
        /// </summary>
        /// <param name="scope">"upscaler", "all", or "auto" (upscaler plus effects the game already ships).</param>
        public Fsr4SwapResult SwapFsr4Dlls(ManagedGame game, string packagePath, string version, string scope = "auto")
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            if (!game.IsInstalledInPlaynite) throw new InvalidOperationException("该游戏未安装在 Playnite 中。");
            if (game.IsRunning) throw new InvalidOperationException("请先退出游戏，再替换 FSR4 DLL。");

            lock (operationLock)
            {
                var staging = Path.Combine(dataPath, "Staging", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                try
                {
                    var manifest = backupStore.LoadManifest(game.InstallDirectory);
                    if (manifest != null && !string.Equals(manifest.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase))
                    {
                        RecoverIncomplete(game.InstallDirectory);
                        manifest = backupStore.LoadManifest(game.InstallDirectory);
                    }
                    if (manifest != null && !string.Equals(manifest.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("该游戏上次的操作没有完成，且无法自动恢复。");
                    var hasManifest = manifest != null;
                    var installDirectory = hasManifest && Directory.Exists(manifest.InstalledGameDirectory)
                        ? manifest.InstalledGameDirectory
                        : ResolveInstallDirectory(game);
                    if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
                        throw new DirectoryNotFoundException("无法确定游戏安装目录。");

                    var extractedRoot = PackageExtractor.PreparePackage(packagePath, staging);
                    var packageFiles = Fsr4DllCatalog.FindPackageFiles(extractedRoot);
                    if (packageFiles.Count == 0) throw new InvalidDataException("这个 FSR4 包里没有可识别的 FidelityFX DLL。");
                    var targets = Fsr4DllCatalog.BuildTargets(installDirectory, packageFiles, scope);
                    if (targets.Count == 0) throw new InvalidOperationException("没有需要替换的 FSR4 文件。");

                    if (manifest == null)
                    {
                        manifest = new InstallationManifest
                        {
                            OperationId = Guid.NewGuid().ToString("N"),
                            GameId = game.Id.ToString("D"),
                            GameInstallDirectory = game.InstallDirectory,
                            InstalledGameDirectory = installDirectory,
                            InstallDateUtc = DateTime.UtcNow.ToString("O"),
                            IncludesOptiScaler = false
                        };
                    }

                    // The journal records only what this swap adds, so rolling it back (now or during
                    // startup recovery) never touches files of the OptiScaler install it joined.
                    var journal = new InstallationManifest
                    {
                        OperationId = manifest.OperationId,
                        OperationStatus = "in_progress",
                        GameId = manifest.GameId,
                        GameInstallDirectory = game.InstallDirectory,
                        InstalledGameDirectory = installDirectory,
                        IncludesOptiScaler = false,
                        IncludesFsr4Swap = true,
                        PreviousManifest = hasManifest ? backupStore.LoadManifest(game.InstallDirectory) : null
                    };
                    backupStore.DeleteUpdateFiles(game.InstallDirectory);
                    backupStore.SaveManifest(game.InstallDirectory, journal);

                    var result = new Fsr4SwapResult();
                    try
                    {
                        foreach (var target in targets)
                        {
                            var relative = BackupStore.NormalizeRelative(PackageExtractor.MakeRelativePath(installDirectory, target.TargetPath));
                            SwapTrackedFile(game.InstallDirectory, installDirectory, relative, target.SourcePath, manifest, journal);
                            result.Files.Add(relative);
                        }

                        manifest.OperationStatus = "committed";
                        manifest.IncludesFsr4Swap = true;
                        manifest.Fsr4SwapVersion = string.IsNullOrWhiteSpace(version) ? Path.GetFileNameWithoutExtension(packagePath) : version;
                        manifest.Fsr4SwapPackagePath = packagePath;
                        manifest.Fsr4SwapScope = scope;
                        foreach (var file in result.Files)
                            if (!manifest.Fsr4SwapFiles.Contains(file, StringComparer.OrdinalIgnoreCase)) manifest.Fsr4SwapFiles.Add(file);
                        if (!manifest.Components.Contains("FSR4 Swap", StringComparer.OrdinalIgnoreCase)) manifest.Components.Add("FSR4 Swap");
                        backupStore.SaveManifest(game.InstallDirectory, manifest);
                        backupStore.DeleteUpdateFiles(game.InstallDirectory);
                        return result;
                    }
                    catch (Exception ex)
                    {
                        try { RollbackOperation(game.InstallDirectory, installDirectory, journal, ex.Message); }
                        catch { }
                        // A swap-only attempt has no previous state to return to. Once every original
                        // is back and every added file is gone, the journal is just noise.
                        if (!hasManifest)
                        {
                            var left = backupStore.LoadManifest(game.InstallDirectory);
                            if (left != null && !string.Equals(left.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase) &&
                                left.FilesCreated.All(x => !File.Exists(Path.Combine(installDirectory, BackupStore.NormalizeRelative(x.RelativePath)))) &&
                                left.FilesOverwritten.All(x => BackupStore.HasSameSha256(Path.Combine(installDirectory, BackupStore.NormalizeRelative(x.RelativePath)), x.PreInstallSha256)))
                                TryDeleteDirectory(backupStore.GetBackupRoot(game.InstallDirectory));
                        }
                        throw;
                    }
                }
                finally
                {
                    TryDeleteDirectory(staging);
                }
            }
        }

        // Copies one swap file and records it. The journal is saved before the game file is written,
        // and a write that fails part-way is undone here because its hash matches neither side.
        private void SwapTrackedFile(string gameRoot, string installDirectory, string relative, string source, InstallationManifest manifest, InstallationManifest journal)
        {
            var destination = Path.Combine(installDirectory, relative);
            var expectedHash = BackupStore.ComputeSha256(source);
            Func<ManifestFileRecord, bool> sameFile = x => string.Equals(BackupStore.NormalizeRelative(x.RelativePath), relative, StringComparison.OrdinalIgnoreCase);
            var record = manifest.FilesOverwritten.LastOrDefault(sameFile) ?? manifest.FilesCreated.LastOrDefault(sameFile);
            var isNewRecord = record == null;
            Action undo;

            if (isNewRecord && File.Exists(destination))
            {
                // A genuine game file: back it up once in the original-file store.
                if (!backupStore.BackupFile(gameRoot, installDirectory, relative)) throw new IOException("无法备份原始文件：" + relative);
                record = new ManifestFileRecord { RelativePath = relative, BackupRelativePath = relative, ExistedBefore = true, PreInstallSha256 = BackupStore.ComputeSha256(destination) };
                manifest.FilesOverwritten.Add(record);
                journal.FilesOverwritten.Add(record);
                undo = () => backupStore.RestoreFile(gameRoot, installDirectory, relative);
            }
            else if (isNewRecord)
            {
                record = new ManifestFileRecord { RelativePath = relative, BackupRelativePath = relative };
                manifest.FilesCreated.Add(record);
                journal.FilesCreated.Add(record);
                undo = () => TryDeleteFile(destination);
            }
            else
            {
                // Already tracked: never re-backup into "files", that would replace the real original.
                if (backupStore.BackupUpdateFile(gameRoot, installDirectory, relative)) journal.UpdateBackupFiles.Add(relative);
                var ownedByOtherInstall = !manifest.Fsr4SwapFiles.Contains(relative, StringComparer.OrdinalIgnoreCase);
                if (ownedByOtherInstall && !manifest.Fsr4PreservedFiles.Contains(relative, StringComparer.OrdinalIgnoreCase) &&
                    backupStore.BackupScopedFile(gameRoot, installDirectory, relative, Fsr4PreservedScope))
                    manifest.Fsr4PreservedFiles.Add(relative);
                undo = () => backupStore.RestoreUpdateFile(gameRoot, installDirectory, relative);
            }
            record.PostInstallSha256 = expectedHash;
            if (!manifest.InstalledFiles.Contains(relative, StringComparer.OrdinalIgnoreCase)) manifest.InstalledFiles.Add(relative);
            backupStore.SaveManifest(gameRoot, journal);

            try
            {
                File.Copy(source, destination, true);
            }
            catch
            {
                try { undo(); } catch { }
                throw;
            }
        }

        /// <summary>
        /// Undoes FSR4 swaps only. Game originals come back from backup, files the swap added are
        /// removed, and files owned by the OptiScaler install return to their pre-swap copy. A
        /// swap-only manifest is deleted afterwards; an OptiScaler install stays managed.
        /// </summary>
        public UninstallResult RestoreFsr4Dlls(ManagedGame game)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            if (game.IsRunning) throw new InvalidOperationException("请先退出游戏，再还原 FSR4 DLL。");
            lock (operationLock)
            {
                var manifest = backupStore.LoadManifest(game.InstallDirectory);
                if (manifest == null || !manifest.IncludesFsr4Swap || !string.Equals(manifest.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("这个游戏没有由本插件替换过的 FSR4 DLL。");
                var installDirectory = manifest.InstalledGameDirectory;
                if (!Directory.Exists(installDirectory)) throw new DirectoryNotFoundException(installDirectory);

                var result = new UninstallResult();
                foreach (var swapped in manifest.Fsr4SwapFiles.ToList())
                {
                    var relative = BackupStore.NormalizeRelative(swapped);
                    var path = Path.Combine(installDirectory, relative);
                    Func<ManifestFileRecord, bool> sameFile = x => string.Equals(BackupStore.NormalizeRelative(x.RelativePath), relative, StringComparison.OrdinalIgnoreCase);
                    var overwritten = manifest.FilesOverwritten.LastOrDefault(sameFile);
                    var created = manifest.FilesCreated.LastOrDefault(sameFile);
                    var record = overwritten ?? created;
                    if (record != null && File.Exists(path) && !BackupStore.HasSameSha256(path, record.PostInstallSha256))
                    {
                        result.Conflicts.Add(relative);
                        continue;
                    }

                    if (manifest.Fsr4PreservedFiles.Contains(relative, StringComparer.OrdinalIgnoreCase))
                    {
                        if (!backupStore.RestoreScopedFile(game.InstallDirectory, installDirectory, relative, Fsr4PreservedScope))
                        {
                            result.Conflicts.Add(relative + " (backup missing)");
                            continue;
                        }
                        if (record != null) record.PostInstallSha256 = BackupStore.ComputeSha256(path);
                        manifest.Fsr4PreservedFiles.RemoveAll(x => string.Equals(BackupStore.NormalizeRelative(x), relative, StringComparison.OrdinalIgnoreCase));
                        result.RestoredFiles.Add(relative);
                    }
                    else if (overwritten != null)
                    {
                        if (!backupStore.RestoreFile(game.InstallDirectory, installDirectory, relative, overwritten.BackupRelativePath))
                        {
                            result.Conflicts.Add(relative + " (backup missing)");
                            continue;
                        }
                        manifest.FilesOverwritten.Remove(overwritten);
                        result.RestoredFiles.Add(relative);
                    }
                    else
                    {
                        TryDeleteFile(path);
                        if (created != null) manifest.FilesCreated.Remove(created);
                        result.RemovedFiles.Add(relative);
                    }
                    if (!manifest.FilesOverwritten.Any(sameFile) && !manifest.FilesCreated.Any(sameFile))
                        manifest.InstalledFiles.RemoveAll(x => string.Equals(BackupStore.NormalizeRelative(x), relative, StringComparison.OrdinalIgnoreCase));
                    manifest.Fsr4SwapFiles.Remove(swapped);
                }

                if (manifest.Fsr4SwapFiles.Count > 0)
                {
                    backupStore.SaveManifest(game.InstallDirectory, manifest);
                    throw new IOException("由于文件已被修改，部分 FSR4 DLL 未能还原：" + string.Join("、", result.Conflicts));
                }

                manifest.IncludesFsr4Swap = false;
                manifest.Fsr4SwapVersion = null;
                manifest.Fsr4SwapPackagePath = null;
                manifest.Fsr4SwapScope = null;
                manifest.Components.RemoveAll(x => string.Equals(x, "FSR4 Swap", StringComparison.OrdinalIgnoreCase));
                if (!manifest.IncludesOptiScaler && manifest.FilesCreated.Count == 0 && manifest.FilesOverwritten.Count == 0)
                {
                    backupStore.Delete(game.InstallDirectory);
                }
                else
                {
                    backupStore.DeleteScope(game.InstallDirectory, Fsr4PreservedScope);
                    backupStore.SaveManifest(game.InstallDirectory, manifest);
                }
                return result;
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

                // Files OptiScaler owned but FSR4 Swap replaced are recorded with the swapped hash;
                // putting the installed copy back first lets the normal checks below match it.
                if (manifest.IncludesFsr4Swap)
                {
                    foreach (var relative in manifest.Fsr4PreservedFiles.ToList())
                    {
                        var normalized = BackupStore.NormalizeRelative(relative);
                        var path = Path.Combine(installDirectory, normalized);
                        var record = manifest.FilesOverwritten.Concat(manifest.FilesCreated).LastOrDefault(x =>
                            string.Equals(BackupStore.NormalizeRelative(x.RelativePath), normalized, StringComparison.OrdinalIgnoreCase));
                        if (record == null || (File.Exists(path) && !BackupStore.HasSameSha256(path, record.PostInstallSha256))) continue;
                        if (!backupStore.RestoreScopedFile(game.InstallDirectory, installDirectory, normalized, Fsr4PreservedScope)) continue;
                        record.PostInstallSha256 = BackupStore.ComputeSha256(path);
                        manifest.Fsr4PreservedFiles.Remove(relative);
                    }
                }

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

            candidates.AddRange(FindBinariesWin64(game.InstallDirectory));
            candidates.Add(game.InstallDirectory);
            var ordered = candidates
                .Where(x => !string.IsNullOrWhiteSpace(x) && Directory.Exists(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(RankInstallDirectory)
                .ToList();
            return ordered.Count > 0 ? ordered[0] : game.InstallDirectory;
        }

        /// <summary>
        /// Unreal ships the game in "&lt;Project&gt;\Binaries\Win64", while "Engine\Binaries\Win64" only
        /// holds engine helpers such as the crash reporter, so the engine copy must never win.
        /// </summary>
        private static int RankInstallDirectory(string directory)
        {
            if (IsBinariesWin64(directory)) return IsEngineBinaries(directory) ? 3 : 0;
            return 1;
        }

        private static bool IsBinariesWin64(string directory)
        {
            var parent = Path.GetDirectoryName(directory);
            return parent != null &&
                   string.Equals(Path.GetFileName(directory), "Win64", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(Path.GetFileName(parent), "Binaries", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsEngineBinaries(string directory)
        {
            if (!IsBinariesWin64(directory)) return false;
            var parent = Path.GetDirectoryName(Path.GetDirectoryName(directory));
            return parent != null && string.Equals(Path.GetFileName(parent), "Engine", StringComparison.OrdinalIgnoreCase);
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
            // Project folders before Engine, so callers that take the first hit get the game's copy.
            return result.OrderBy(IsEngineBinaries).ToList();
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
