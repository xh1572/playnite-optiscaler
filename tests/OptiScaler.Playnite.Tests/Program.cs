using System;
using System.IO;
using System.Linq;
using OptiScaler.Playnite.Core.Archive;
using OptiScaler.Playnite.Core.Models;
using OptiScaler.Playnite.Core.Services;
using OptiScaler.Playnite.Core.Storage;
using OptiScaler.Playnite.Views;

namespace OptiScaler.Playnite.Tests
{
    internal static class Program
    {
        [STAThread]
        private static int Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "OptiScaler.Playnite.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                AssertThrows<InvalidDataException>(() => BackupStore.NormalizeRelative("..\\outside.dll"));
                AssertThrows<InvalidDataException>(() => BackupStore.NormalizeRelative("nested\\..\\outside.dll"));
                var relative = PackageExtractor.MakeRelativePath(root, Path.Combine(root, "nested", "file.dll"));
                Assert(string.Equals(relative, "nested" + Path.DirectorySeparatorChar + "file.dll", StringComparison.OrdinalIgnoreCase), "relative path conversion");

                var store = new BackupStore(Path.Combine(root, "data"));
                var manifest = new InstallationManifest
                {
                    OperationId = "test",
                    OperationStatus = "committed",
                    GameInstallDirectory = root,
                    InstalledGameDirectory = root
                };
                store.SaveManifest(root, manifest);
                Assert(store.HasValidManifest(root), "manifest round trip");
                Assert(store.LoadManifest(root).OperationId == "test", "manifest data");

                var profileStore = new ProfileService(Path.Combine(root, "profiles"));
                profileStore.Save("default", "; keep this comment\r\n[FrameGen]\r\nEnabled=false\r\n");
                Assert(profileStore.ListProfiles().Contains("default"), "profile listing");
                Assert(profileStore.Load("default").Contains("Enabled=false"), "profile load");
                AssertThrows<ArgumentException>(() => profileStore.Save("..\\escape", "bad"));
                profileStore.Delete("default");
                Assert(!profileStore.ListProfiles().Contains("default"), "profile delete");

                var ini = IniDocumentViewModel.Parse("; keep this comment\r\n[FrameGen]\r\nEnabled=false\r\n");
                Assert(ini.GetValue("FrameGen", "Enabled") == "false", "ini read");
                ini.SetValue("FrameGen", "Enabled", "true");
                var serialized = ini.ToText();
                Assert(serialized.Contains("; keep this comment"), "ini comment preservation");
                Assert(serialized.Contains("Enabled=true"), "ini write");
                var editorWindow = new ConfigurationEditorWindow("Temporary", serialized, profileStore);
                editorWindow.Close();

                RunSyntheticTransactionTests(root);

                var package = Environment.GetEnvironmentVariable("OPTISCALER_TEST_PACKAGE");
                if (!string.IsNullOrWhiteSpace(package) && File.Exists(package)) RunInstallationSmokeTest(root, package);
                Console.WriteLine("CORE TEST PASS");
                return 0;
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void Assert(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Assertion failed: " + name);
        }

        private static void AssertThrows<T>(Action action) where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }
            throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
        }

        // Uses directory packages with fake DLLs so rollback, conflict and corruption paths run
        // without a real OptiScaler download and only inside the temporary test root.
        private static void RunSyntheticTransactionTests(string root)
        {
            var packageV1 = CreatePackage(root, "OptiScaler_1.0.0", "v1", "zz_extra.dll");
            var packageV2 = CreatePackage(root, "OptiScaler_2.0.0", "v2", "zz_locked.dll");

            // Failed first install: a read-only game file blocks the copy, everything is undone.
            var game = CreateGame(root, "fresh");
            var dir = game.InstallDirectory;
            var original = Path.Combine(dir, "zz_extra.dll");
            File.WriteAllText(original, "game-owned");
            File.SetAttributes(original, FileAttributes.ReadOnly);
            var service = new InstallationService(Path.Combine(root, "data-fresh"));
            AssertThrows<UnauthorizedAccessException>(() => service.Install(game, packageV1, "dxgi.dll", "1.0.0"));
            Assert(!File.Exists(Path.Combine(dir, "dxgi.dll")) && !File.Exists(Path.Combine(dir, "OptiScaler.ini")), "failed install rollback");
            Assert(File.ReadAllText(original) == "game-owned", "failed install keeps game file");
            File.SetAttributes(original, FileAttributes.Normal);

            // Failed update: the previous committed version must be fully restored.
            game = CreateGame(root, "update");
            dir = game.InstallDirectory;
            var dataPath = Path.Combine(root, "data-update");
            service = new InstallationService(dataPath);
            service.Install(game, packageV1, "dxgi.dll", "1.0.0");
            var blocker = Path.Combine(dir, "zz_locked.dll");
            File.WriteAllText(blocker, "game-owned");
            File.SetAttributes(blocker, FileAttributes.ReadOnly);
            AssertThrows<UnauthorizedAccessException>(() => service.Install(game, packageV2, "dxgi.dll", "2.0.0"));
            File.SetAttributes(blocker, FileAttributes.Normal);
            Assert(File.ReadAllText(Path.Combine(dir, "dxgi.dll")) == "v1-main", "failed update restores main dll");
            Assert(File.ReadAllText(Path.Combine(dir, "zz_extra.dll")) == "v1-zz_extra.dll", "failed update restores component");
            var store = new BackupStore(dataPath);
            var restored = store.LoadManifest(dir);
            Assert(restored != null && restored.OperationStatus == "committed" && restored.OptiScalerVersion == "1.0.0", "failed update restores manifest");
            Assert(new OptiScalerAnalyzer(dataPath).Analyze(game).State == OptiScalerInstallState.Installed, "failed update analyzer state");

            // Successful update removes stale files and keeps the user's ini.
            File.Delete(blocker);
            File.WriteAllText(Path.Combine(dir, "OptiScaler.ini"), "user-edited");
            var updated = service.Install(game, packageV2, "dxgi.dll", "2.0.0");
            Assert(updated.PreviousManifest == null && updated.UpdateBackupFiles.Count == 0, "update clears rollback state");
            Assert(!File.Exists(Path.Combine(dir, "zz_extra.dll")), "update removes stale file");
            Assert(File.ReadAllText(Path.Combine(dir, "OptiScaler.ini")) == "user-edited", "update keeps ini");

            // Uninstall conflict: a user-modified file is reported and left in place.
            File.WriteAllText(Path.Combine(dir, "zz_locked.dll"), "user-modified");
            AssertThrows<IOException>(() => service.Uninstall(game));
            Assert(File.ReadAllText(Path.Combine(dir, "zz_locked.dll")) == "user-modified", "conflict keeps modified file");
            Assert(!File.Exists(Path.Combine(dir, "dxgi.dll")), "conflict still removes unmodified files");

            // Corrupt manifest: analyzer and uninstall fail safely instead of crashing.
            game = CreateGame(root, "corrupt");
            dataPath = Path.Combine(root, "data-corrupt");
            store = new BackupStore(dataPath);
            Directory.CreateDirectory(store.GetBackupRoot(game.InstallDirectory));
            File.WriteAllText(store.GetManifestPath(game.InstallDirectory), "{ not json");
            Assert(new OptiScalerAnalyzer(dataPath).Analyze(game).State == OptiScalerInstallState.NotInstalled, "corrupt manifest analysis");
            AssertThrows<InvalidOperationException>(() => new InstallationService(dataPath).Uninstall(game));

            // Unreal layout: Binaries\Win64 wins over the launcher stub in the game root.
            game = CreateGame(root, "unreal");
            var win64 = Path.Combine(game.InstallDirectory, "Game", "Binaries", "Win64");
            Directory.CreateDirectory(win64);
            File.WriteAllText(Path.Combine(game.InstallDirectory, "Launcher.exe"), "stub");
            game.ExecutablePaths.Add(Path.Combine(game.InstallDirectory, "Launcher.exe"));
            Assert(string.Equals(new InstallationService(Path.Combine(root, "data-unreal")).ResolveInstallDirectory(game), win64, StringComparison.OrdinalIgnoreCase), "unreal directory resolution");
        }

        private static string CreatePackage(string root, string name, string tag, string extraFile)
        {
            var directory = Path.Combine(root, "packages", name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "OptiScaler.dll"), tag + "-main");
            File.WriteAllText(Path.Combine(directory, "OptiScaler.ini"), "[FrameGen]\r\nEnabled=false\r\n");
            File.WriteAllText(Path.Combine(directory, extraFile), tag + "-" + extraFile);
            return directory;
        }

        private static ManagedGame CreateGame(string root, string name)
        {
            var directory = Path.Combine(root, "games", name);
            Directory.CreateDirectory(directory);
            return new ManagedGame
            {
                Id = Guid.NewGuid(),
                Name = name,
                InstallDirectory = directory,
                IsInstalledInPlaynite = true
            };
        }

        private static void RunInstallationSmokeTest(string root, string package)
        {
            var gameRoot = Path.Combine(root, "game");
            var executableDirectory = Path.Combine(gameRoot, "Binaries", "Win64");
            Directory.CreateDirectory(executableDirectory);
            var executable = Path.Combine(executableDirectory, "test-game.exe");
            File.WriteAllBytes(executable, new byte[] { 0x4D, 0x5A });
            var game = new ManagedGame
            {
                Id = Guid.NewGuid(),
                Name = "Temporary OptiScaler Test",
                InstallDirectory = gameRoot,
                IsInstalledInPlaynite = true,
                ExecutablePaths = new System.Collections.Generic.List<string> { executable }
            };
            var service = new InstallationService(Path.Combine(root, "install-data"));
            var profile = "; profile test\r\n[FrameGen]\r\nEnabled=false\r\n";
            var manifest = service.Install(game, package, "dxgi.dll", null, profile, "test-profile");
            Assert(!string.IsNullOrWhiteSpace(manifest.OptiScalerVersion), "package version detection");
            Assert(manifest.AppliedProfileName == "test-profile", "profile manifest");
            var installedIni = Path.Combine(executableDirectory, "OptiScaler.ini");
            Assert(File.Exists(installedIni) && File.ReadAllText(installedIni).Contains("profile test"), "profile installation");
            service.Uninstall(game);
            Assert(File.Exists(executable) && Directory.GetFiles(executableDirectory).Length == 1, "installation cleanup");
        }
    }
}
