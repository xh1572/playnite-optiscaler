using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using OptiScaler.Playnite.Core.Models;

namespace OptiScaler.Playnite.Core.Storage
{
    public sealed class BackupStore
    {
        private const string ManifestFileName = "manifest.json";
        private const string FilesDirectoryName = "files";
        private const string UpdateFilesDirectoryName = "update-files";
        private readonly string root;

        public BackupStore(string pluginDataPath)
        {
            root = Path.Combine(pluginDataPath, "Backups");
            Directory.CreateDirectory(root);
        }

        public string GetBackupRoot(string gameRoot) => Path.Combine(root, ComputeGameSlug(gameRoot));
        public string GetFilesDirectory(string gameRoot) => Path.Combine(GetBackupRoot(gameRoot), FilesDirectoryName);
        public string GetManifestPath(string gameRoot) => Path.Combine(GetBackupRoot(gameRoot), ManifestFileName);

        public bool HasValidManifest(string gameRoot)
        {
            var manifest = LoadManifest(gameRoot);
            return manifest != null && string.Equals(manifest.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Imports a committed OptiScaler Client backup when its recorded install directory
        /// matches the directory Playnite resolved for this game. The source backup is left intact
        /// so the standalone client can still recover the installation if needed.
        /// </summary>
        public bool TryMigrateLegacyClient(string gameRoot, string installedDirectory, Guid gameId)
        {
            if (string.IsNullOrWhiteSpace(gameRoot) || string.IsNullOrWhiteSpace(installedDirectory) ||
                LoadManifest(gameRoot) != null)
                return false;

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var legacyRoot = Path.Combine(appData, "OptiscalerClient", "Backups");
            if (!Directory.Exists(legacyRoot)) return false;

            var normalizedInstalled = NormalizeDirectory(installedDirectory);
            string[] entries;
            try { entries = Directory.GetDirectories(legacyRoot); }
            catch (UnauthorizedAccessException) { return false; }
            catch (IOException) { return false; }
            foreach (var entry in entries)
            {
                var legacyManifestPath = Path.Combine(entry, ManifestFileName);
                if (!File.Exists(legacyManifestPath)) continue;
                InstallationManifest legacy;
                try { legacy = JsonConvert.DeserializeObject<InstallationManifest>(File.ReadAllText(legacyManifestPath)); }
                catch { continue; }
                if (legacy == null || !string.Equals(legacy.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(NormalizeDirectory(legacy.InstalledGameDirectory), normalizedInstalled, StringComparison.OrdinalIgnoreCase))
                    continue;

                var migrated = legacy;
                migrated.GameId = gameId.ToString("D");
                migrated.GameInstallDirectory = NormalizeDirectory(gameRoot);
                migrated.InstalledGameDirectory = NormalizeDirectory(installedDirectory);
                migrated.MigrationSource = "optiscaler-client-external";
                migrated.BackedUpFiles = migrated.BackedUpFiles ?? new List<string>();
                migrated.FilesOverwritten = migrated.FilesOverwritten ?? new List<ManifestFileRecord>();
                migrated.FilesCreated = migrated.FilesCreated ?? new List<ManifestFileRecord>();

                var legacyFiles = Path.Combine(entry, FilesDirectoryName);
                foreach (var record in migrated.FilesOverwritten)
                {
                    var backupRelative = string.IsNullOrWhiteSpace(record.BackupRelativePath) ? record.RelativePath : record.BackupRelativePath;
                    TryCopyLegacyBackup(legacyFiles, gameRoot, backupRelative);
                    if (string.IsNullOrWhiteSpace(record.PostInstallSha256))
                        record.PostInstallSha256 = ComputeSha256(Path.Combine(installedDirectory, NormalizeRelative(record.RelativePath)));
                }
                foreach (var relative in migrated.BackedUpFiles)
                {
                    var safeRelative = NormalizeRelative(relative);
                    if (migrated.FilesOverwritten.Any(x => string.Equals(x.RelativePath, relative, StringComparison.OrdinalIgnoreCase))) continue;
                    if (!TryCopyLegacyBackup(legacyFiles, gameRoot, safeRelative)) continue;
                    migrated.FilesOverwritten.Add(new ManifestFileRecord
                    {
                        RelativePath = relative,
                        BackupRelativePath = safeRelative,
                        ExistedBefore = true,
                        PreInstallSha256 = ComputeSha256(Path.Combine(GetFilesDirectory(gameRoot), safeRelative)),
                        PostInstallSha256 = ComputeSha256(Path.Combine(installedDirectory, safeRelative))
                    });
                }

                SaveManifest(gameRoot, migrated);
                return true;
            }
            return false;
        }

        public bool TryMigrateLegacyInFolder(string gameRoot, string installedDirectory, Guid gameId)
        {
            if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot) || LoadManifest(gameRoot) != null)
                return false;

            var normalizedInstalled = NormalizeDirectory(installedDirectory);
            var candidates = new List<string>();
            try
            {
                candidates.AddRange(Directory.GetFiles(gameRoot, "optiscaler_manifest.json", SearchOption.AllDirectories));
            }
            catch { }
            foreach (var legacyManifestPath in candidates)
            {
                InstallationManifest legacy;
                try { legacy = JsonConvert.DeserializeObject<InstallationManifest>(File.ReadAllText(legacyManifestPath)); }
                catch { continue; }
                if (legacy == null || !string.Equals(legacy.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase)) continue;
                var recordedDirectory = string.IsNullOrWhiteSpace(legacy.InstalledGameDirectory)
                    ? installedDirectory
                    : legacy.InstalledGameDirectory;
                if (!string.Equals(NormalizeDirectory(recordedDirectory), normalizedInstalled, StringComparison.OrdinalIgnoreCase)) continue;

                var legacyFiles = Path.GetDirectoryName(legacyManifestPath);
                if (string.IsNullOrWhiteSpace(legacyFiles)) continue;
                legacy.GameId = gameId.ToString("D");
                legacy.GameInstallDirectory = NormalizeDirectory(gameRoot);
                legacy.InstalledGameDirectory = normalizedInstalled;
                legacy.MigrationSource = "optiscaler-client-in-folder";
                legacy.BackedUpFiles = legacy.BackedUpFiles ?? new List<string>();
                legacy.FilesOverwritten = legacy.FilesOverwritten ?? new List<ManifestFileRecord>();
                legacy.FilesCreated = legacy.FilesCreated ?? new List<ManifestFileRecord>();

                foreach (var record in legacy.FilesOverwritten)
                {
                    var backupRelative = string.IsNullOrWhiteSpace(record.BackupRelativePath) ? record.RelativePath : record.BackupRelativePath;
                    TryCopyLegacyBackup(legacyFiles, gameRoot, backupRelative);
                    if (string.IsNullOrWhiteSpace(record.PostInstallSha256))
                        record.PostInstallSha256 = ComputeSha256(Path.Combine(installedDirectory, NormalizeRelative(record.RelativePath)));
                }
                foreach (var relative in legacy.BackedUpFiles)
                {
                    if (legacy.FilesOverwritten.Any(x => string.Equals(x.RelativePath, relative, StringComparison.OrdinalIgnoreCase))) continue;
                    var safeRelative = NormalizeRelative(relative);
                    if (!TryCopyLegacyBackup(legacyFiles, gameRoot, safeRelative)) continue;
                    legacy.FilesOverwritten.Add(new ManifestFileRecord
                    {
                        RelativePath = relative,
                        BackupRelativePath = safeRelative,
                        ExistedBefore = true,
                        PreInstallSha256 = ComputeSha256(Path.Combine(GetFilesDirectory(gameRoot), safeRelative)),
                        PostInstallSha256 = ComputeSha256(Path.Combine(installedDirectory, safeRelative))
                    });
                }
                SaveManifest(gameRoot, legacy);
                return true;
            }
            return false;
        }

        public InstallationManifest LoadManifest(string gameRoot)
        {
            var path = GetManifestPath(gameRoot);
            if (!File.Exists(path)) return null;
            try
            {
                return JsonConvert.DeserializeObject<InstallationManifest>(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        public void SaveManifest(string gameRoot, InstallationManifest manifest)
        {
            var backupRoot = GetBackupRoot(gameRoot);
            Directory.CreateDirectory(backupRoot);
            var target = GetManifestPath(gameRoot);
            var temporary = target + ".tmp";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(manifest, Formatting.Indented), Encoding.UTF8);
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
        }

        public bool BackupFile(string gameRoot, string actualInstallDirectory, string relativePath)
        {
            var source = CombineRelative(actualInstallDirectory, relativePath);
            if (!File.Exists(source)) return false;
            var target = CombineRelative(GetFilesDirectory(gameRoot), relativePath);
            var targetDirectory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDirectory)) Directory.CreateDirectory(targetDirectory);
            File.Copy(source, target, true);
            return true;
        }

        public bool RestoreFile(string gameRoot, string actualInstallDirectory, string relativePath, string backupRelativePath = null)
        {
            var source = CombineRelative(GetFilesDirectory(gameRoot), backupRelativePath ?? relativePath);
            if (!File.Exists(source)) return false;
            var target = CombineRelative(actualInstallDirectory, relativePath);
            var targetDirectory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDirectory)) Directory.CreateDirectory(targetDirectory);
            File.Copy(source, target, true);
            return true;
        }

        /// <summary>
        /// Snapshots a file installed by the previous managed version before an update replaces it.
        /// Kept apart from the original-game backups so a failed update can restore it.
        /// </summary>
        public bool BackupUpdateFile(string gameRoot, string actualInstallDirectory, string relativePath)
            => BackupScopedFile(gameRoot, actualInstallDirectory, relativePath, UpdateFilesDirectoryName);

        public bool RestoreUpdateFile(string gameRoot, string actualInstallDirectory, string relativePath)
            => RestoreScopedFile(gameRoot, actualInstallDirectory, relativePath, UpdateFilesDirectoryName);

        public void DeleteUpdateFiles(string gameRoot) => DeleteScope(gameRoot, UpdateFilesDirectoryName);

        /// <summary>Copies a game file into a named sub-store of this game's backup, apart from the originals in "files".</summary>
        public bool BackupScopedFile(string gameRoot, string actualInstallDirectory, string relativePath, string scope)
        {
            var source = CombineRelative(actualInstallDirectory, relativePath);
            if (!File.Exists(source)) return false;
            var target = CombineRelative(Path.Combine(GetBackupRoot(gameRoot), scope), relativePath);
            var targetDirectory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDirectory)) Directory.CreateDirectory(targetDirectory);
            File.Copy(source, target, true);
            return true;
        }

        public bool RestoreScopedFile(string gameRoot, string actualInstallDirectory, string relativePath, string scope)
        {
            var source = CombineRelative(Path.Combine(GetBackupRoot(gameRoot), scope), relativePath);
            if (!File.Exists(source)) return false;
            var target = CombineRelative(actualInstallDirectory, relativePath);
            var targetDirectory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDirectory)) Directory.CreateDirectory(targetDirectory);
            File.Copy(source, target, true);
            return true;
        }

        public void DeleteScope(string gameRoot, string scope)
        {
            var path = Path.Combine(GetBackupRoot(gameRoot), scope);
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        public void Delete(string gameRoot)
        {
            var backupRoot = GetBackupRoot(gameRoot);
            if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, true);
        }

        public static string ComputeSha256(string path)
        {
            if (!File.Exists(path)) return null;
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        public static bool HasSameSha256(string path, string expected)
        {
            if (string.IsNullOrWhiteSpace(expected)) return false;
            var actual = ComputeSha256(path);
            return actual != null && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        public static string NormalizeDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        }

        public static string NormalizeRelative(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Relative path is empty.", nameof(path));
            var normalized = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            var segments = normalized.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            if (Path.IsPathRooted(normalized) || segments.Any(x => string.Equals(x, "..", StringComparison.Ordinal)))
                throw new InvalidDataException("路径超出了目标目录：" + path);
            return normalized;
        }

        private static string CombineRelative(string rootDirectory, string relativePath)
        {
            var relative = NormalizeRelative(relativePath);
            var rootFull = NormalizeDirectory(rootDirectory) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(rootFull, relative));
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("路径超出了目标目录：" + relativePath);
            return full;
        }

        private static string ComputeGameSlug(string gameRoot)
        {
            var normalized = NormalizeDirectory(gameRoot).ToLowerInvariant();
            using (var sha = SHA256.Create())
            {
                var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(normalized))).Replace("-", string.Empty).Substring(0, 8).ToLowerInvariant();
                var parts = normalized.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                var readable = string.Join("_", parts.Skip(Math.Max(0, parts.Length - 2)).Select(SanitizeSegment));
                return readable + "_" + hash;
            }
        }

        private bool TryCopyLegacyBackup(string legacyFiles, string gameRoot, string relativePath)
        {
            try
            {
                var source = CombineRelative(legacyFiles, relativePath);
                if (!File.Exists(source)) return false;
                var target = CombineRelative(GetFilesDirectory(gameRoot), relativePath);
                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                File.Copy(source, target, true);
                return true;
            }
            catch { return false; }
        }

        private static string SanitizeSegment(string segment)
        {
            var chars = segment.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray();
            return new string(chars);
        }
    }
}
