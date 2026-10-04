using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SharpCompress.Archives;
using SharpCompress.Readers;
using SharpCompress.Common;
using OptiScaler.Playnite.Core.Storage;

namespace OptiScaler.Playnite.Core.Archive
{
    public static class PackageExtractor
    {
        public static string PreparePackage(string packagePath, string stagingRoot)
        {
            if (Directory.Exists(packagePath)) return Path.GetFullPath(packagePath);
            if (!File.Exists(packagePath)) throw new FileNotFoundException("没有找到 OptiScaler 安装包。", packagePath);

            Directory.CreateDirectory(stagingRoot);
            var destination = Path.Combine(stagingRoot, "extracted");
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            Directory.CreateDirectory(destination);

            using (var archive = ArchiveFactory.Open(packagePath, new ReaderOptions()))
            {
                // Validate every entry before handing extraction to SharpCompress. This keeps
                // the fast bulk extractor while retaining the plugin's traversal protection.
                foreach (var entry in archive.Entries.Where(x => !x.IsDirectory))
                {
                    var relative = BackupStore.NormalizeRelative(entry.Key);
                    var target = Path.GetFullPath(Path.Combine(destination, relative));
                    var root = destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("安装包包含越界文件路径：" + entry.Key);
                }
            }
            if (TryExtractWithSevenZip(packagePath, destination)) return destination;

            using (var archive = ArchiveFactory.Open(packagePath, new ReaderOptions()))
                archive.WriteToDirectory(destination, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
            return destination;
        }

        private static bool TryExtractWithSevenZip(string packagePath, string destination)
        {
            try
            {
                var executable = FindSevenZip();
                if (string.IsNullOrWhiteSpace(executable)) return false;
                var startInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    // -p- disables password prompts so encrypted archives fail instead of waiting on stdin.
                    Arguments = "x -y -bd -p- -o\"" + QuoteArgument(destination) + "\" \"" + QuoteArgument(packagePath) + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    WorkingDirectory = destination
                };
                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return false;
                    process.StandardInput.Close();
                    if (!process.WaitForExit(120000))
                    {
                        try { process.Kill(); } catch { }
                        return false;
                    }
                    return process.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string FindSevenZip()
        {
            var names = new[] { "7z.exe", "7za.exe" };
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var root in path.Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries))
                foreach (var name in names)
                {
                    try
                    {
                        var candidate = Path.Combine(root.Trim().Trim('"'), name);
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch (ArgumentException)
                    {
                        // Ignore malformed PATH entries.
                    }
                }

            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
            foreach (var root in roots.Where(x => !string.IsNullOrWhiteSpace(x)))
                foreach (var name in names)
                {
                    var candidate = Path.Combine(root, "7-Zip", name);
                    if (File.Exists(candidate)) return candidate;
                }
            return null;
        }

        private static string QuoteArgument(string value) => (value ?? string.Empty).Replace("\"", "\\\"");

        public static string FindPackageRoot(string extractedDirectory)
        {
            var candidates = Directory.GetFiles(extractedDirectory, "OptiScaler.dll", SearchOption.AllDirectories);
            if (candidates.Length == 0)
                candidates = Directory.GetFiles(extractedDirectory, "nvngx.dll", SearchOption.AllDirectories);
            if (candidates.Length == 0)
                throw new InvalidDataException("安装包中没有 OptiScaler.dll 或 nvngx.dll。");
            return Path.GetDirectoryName(candidates[0]);
        }

        public static string MakeRelativePath(string root, string path)
        {
            var rootUri = new Uri(AppendDirectorySeparator(Path.GetFullPath(root)));
            var pathUri = new Uri(Path.GetFullPath(path));
            var relative = Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString());
            return relative.Replace('/', Path.DirectorySeparatorChar);
        }

        private static string AppendDirectorySeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? path : path + Path.DirectorySeparatorChar;
        }
    }
}
