using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace OptiScaler.Playnite.Core.Services
{
    /// <summary>Stores user OptiScaler.ini profiles in the plugin data directory.</summary>
    public sealed class ProfileService
    {
        private readonly string profilesDirectory;

        public ProfileService(string pluginDataPath)
        {
            profilesDirectory = Path.Combine(pluginDataPath ?? string.Empty, "Profiles");
            Directory.CreateDirectory(profilesDirectory);
        }

        public IReadOnlyList<string> ListProfiles()
        {
            try
            {
                return Directory.GetFiles(profilesDirectory, "*.ini", SearchOption.TopDirectoryOnly)
                    .Select(x => Path.GetFileNameWithoutExtension(x))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (DirectoryNotFoundException)
            {
                Directory.CreateDirectory(profilesDirectory);
                return new List<string>();
            }
        }

        public string Load(string name)
        {
            var path = GetProfilePath(name);
            if (!File.Exists(path)) throw new FileNotFoundException("没有找到配置 profile。", path);
            return File.ReadAllText(path);
        }

        public void Save(string name, string contents)
        {
            var path = GetProfilePath(name);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, contents ?? string.Empty, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        public void Delete(string name)
        {
            var path = GetProfilePath(name);
            if (File.Exists(path)) File.Delete(path);
        }

        private string GetProfilePath(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("请输入 profile 名称。", nameof(name));
            var trimmed = name.Trim();
            if (trimmed.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) trimmed = Path.GetFileNameWithoutExtension(trimmed);
            if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                trimmed == "." || trimmed == ".." ||
                trimmed.IndexOf(Path.DirectorySeparatorChar) >= 0 || trimmed.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
                throw new ArgumentException("profile 名称包含非法字符。", nameof(name));
            var path = Path.GetFullPath(Path.Combine(profilesDirectory, trimmed + ".ini"));
            var root = Path.GetFullPath(profilesDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("profile 名称无效。", nameof(name));
            return path;
        }
    }
}
