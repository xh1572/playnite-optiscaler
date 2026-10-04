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
                var fsr4Package = Environment.GetEnvironmentVariable("OPTISCALER_TEST_FSR4_PACKAGE");
                if (!string.IsNullOrWhiteSpace(fsr4Package) && File.Exists(fsr4Package)) RunFsr4PackageSmokeTest(root, fsr4Package);
                Console.WriteLine("CORE TEST PASS");
                return 0;
            }
            catch (Exception ex)
            {
                // Write plain fields: the default console output can fail on localized messages.
                var report = ex.GetType().FullName + ": " + ex.Message + Environment.NewLine + ex.StackTrace;
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "OptiScaler.Playnite.Tests.failure.txt"), report, new System.Text.UTF8Encoding(false)); } catch { }
                Console.WriteLine("CORE TEST FAIL");
                return 1;
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

            RunFsr4SwapTests(root);

            // Unreal layout: Binaries\Win64 wins over the launcher stub in the game root.
            game = CreateGame(root, "unreal");
            var win64 = Path.Combine(game.InstallDirectory, "Game", "Binaries", "Win64");
            Directory.CreateDirectory(win64);
            File.WriteAllText(Path.Combine(game.InstallDirectory, "Launcher.exe"), "stub");
            game.ExecutablePaths.Add(Path.Combine(game.InstallDirectory, "Launcher.exe"));
            Assert(string.Equals(new InstallationService(Path.Combine(root, "data-unreal")).ResolveInstallDirectory(game), win64, StringComparison.OrdinalIgnoreCase), "unreal directory resolution");
        }

        private static void RunFsr4SwapTests(string root)
        {
            var upscaler = Fsr4DllCatalog.LegacyUpscalerFileName;
            var fsr4Package = Path.Combine(root, "packages", "FSR4_INT8_test");
            Directory.CreateDirectory(fsr4Package);
            File.WriteAllText(Path.Combine(fsr4Package, upscaler), "fsr4-int8");
            File.WriteAllText(Path.Combine(fsr4Package, "amd_fidelityfx_loader_dx12.dll"), "fsr4-loader");

            // Swap-only: replace the game's own DLL, skip effects it does not ship, restore cleanly.
            var game = CreateGame(root, "fsr4-only");
            var dir = game.InstallDirectory;
            File.WriteAllText(Path.Combine(dir, upscaler), "game-fsr3");
            var dataPath = Path.Combine(root, "data-fsr4");
            var service = new InstallationService(dataPath);
            var swap = service.SwapFsr4Dlls(game, fsr4Package, "FSR 4.0.2c", "auto");
            Assert(swap.Files.Count == 1 && File.ReadAllText(Path.Combine(dir, upscaler)) == "fsr4-int8", "fsr4 swap replaces upscaler");
            Assert(!File.Exists(Path.Combine(dir, "amd_fidelityfx_loader_dx12.dll")), "fsr4 auto scope skips missing effects");
            var status = new OptiScalerAnalyzer(dataPath).Analyze(game);
            Assert(status.HasFsr4Swap && status.IsSwapOnly && status.State == OptiScalerInstallState.NotInstalled, "fsr4 swap-only status");
            // Swapping again must not overwrite the real original in the backup.
            service.SwapFsr4Dlls(game, fsr4Package, "FSR 4.0.2c", "auto");
            service.RestoreFsr4Dlls(game);
            Assert(File.ReadAllText(Path.Combine(dir, upscaler)) == "game-fsr3", "fsr4 restore brings back original");
            Assert(new BackupStore(dataPath).LoadManifest(dir) == null, "fsr4 swap-only restore removes manifest");

            // "all" scope adds files the game did not have; restore deletes them again.
            service.SwapFsr4Dlls(game, fsr4Package, "FSR 4.0.2c", "all");
            Assert(File.Exists(Path.Combine(dir, "amd_fidelityfx_loader_dx12.dll")), "fsr4 all scope adds effects");
            service.RestoreFsr4Dlls(game);
            Assert(!File.Exists(Path.Combine(dir, "amd_fidelityfx_loader_dx12.dll")) && File.ReadAllText(Path.Combine(dir, upscaler)) == "game-fsr3", "fsr4 restore removes added files");

            // On top of OptiScaler: swap over the DLL OptiScaler installed, survive an update, then
            // uninstall must remove everything and return the game's original file.
            game = CreateGame(root, "fsr4-opti");
            dir = game.InstallDirectory;
            File.WriteAllText(Path.Combine(dir, upscaler), "game-fsr3");
            dataPath = Path.Combine(root, "data-fsr4-opti");
            service = new InstallationService(dataPath);
            var optiPackage = CreatePackage(root, "OptiScaler_fsr4", "opti", upscaler);
            service.Install(game, optiPackage, "dxgi.dll", "1.0.0");
            Assert(File.ReadAllText(Path.Combine(dir, upscaler)) == "opti-" + upscaler, "optiscaler installs its fsr dll");
            service.SwapFsr4Dlls(game, fsr4Package, "FSR 4.0.2c", "upscaler");
            Assert(File.ReadAllText(Path.Combine(dir, upscaler)) == "fsr4-int8", "fsr4 swap over optiscaler");
            service.Install(game, optiPackage, "dxgi.dll", "1.0.1");
            Assert(File.ReadAllText(Path.Combine(dir, upscaler)) == "fsr4-int8", "fsr4 swap re-applied after update");
            service.RestoreFsr4Dlls(game);
            Assert(File.ReadAllText(Path.Combine(dir, upscaler)) == "opti-" + upscaler, "fsr4 restore returns optiscaler copy");
            service.SwapFsr4Dlls(game, fsr4Package, "FSR 4.0.2c", "upscaler");
            service.Uninstall(game);
            Assert(File.ReadAllText(Path.Combine(dir, upscaler)) == "game-fsr3" && !File.Exists(Path.Combine(dir, "dxgi.dll")), "uninstall after fsr4 swap restores game");

            // A failed swap (locked target) rolls back and leaves no swap-only manifest behind.
            game = CreateGame(root, "fsr4-fail");
            dir = game.InstallDirectory;
            var locked = Path.Combine(dir, upscaler);
            File.WriteAllText(locked, "game-fsr3");
            File.SetAttributes(locked, FileAttributes.ReadOnly);
            dataPath = Path.Combine(root, "data-fsr4-fail");
            AssertThrows<UnauthorizedAccessException>(() => new InstallationService(dataPath).SwapFsr4Dlls(game, fsr4Package, "x", "auto"));
            File.SetAttributes(locked, FileAttributes.Normal);
            Assert(File.ReadAllText(locked) == "game-fsr3", "failed fsr4 swap keeps original");
            Assert(new OptiScalerAnalyzer(dataPath).Analyze(game).State == OptiScalerInstallState.NotInstalled, "failed fsr4 swap analyzer state");
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

        // Real OptiScaler Extras archive (.7z) against a temporary game folder only.
        private static void RunFsr4PackageSmokeTest(string root, string package)
        {
            var game = CreateGame(root, "fsr4-real");
            var original = Path.Combine(game.InstallDirectory, Fsr4DllCatalog.LegacyUpscalerFileName);
            File.WriteAllText(original, "game-fsr3");
            var service = new InstallationService(Path.Combine(root, "data-fsr4-real"));
            var result = service.SwapFsr4Dlls(game, package, Path.GetFileNameWithoutExtension(package), "auto");
            Assert(result.Files.Count >= 1 && new FileInfo(original).Length > 1024 * 1024, "real fsr4 package swap");
            service.RestoreFsr4Dlls(game);
            Assert(File.ReadAllText(original) == "game-fsr3", "real fsr4 package restore");
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
