using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace OptiScaler.Playnite.Core.Services
{
    public sealed class ReleaseInfo
    {
        public string TagName { get; set; }
        public string Name { get; set; }
        public string Channel { get; set; }
        public DateTime PublishedAt { get; set; }
        public string AssetName { get; set; }
        public string AssetUrl { get; set; }
        public bool IsPrerelease { get; set; }
    }

    public sealed class ReleaseService : IDisposable
    {
        private readonly HttpClient client;
        private readonly string cacheDirectory;
        private List<ReleaseInfo> cachedReleases = new List<ReleaseInfo>();

        public ReleaseService(string pluginDataPath)
        {
            cacheDirectory = Path.Combine(pluginDataPath, "Cache", "OptiScaler");
            Directory.CreateDirectory(cacheDirectory);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OptiScaler.Playnite/0.1");
            LoadCachedReleases();
        }

        public IReadOnlyList<ReleaseInfo> GetCachedReleases(string channel)
        {
            var key = NormalizeChannel(channel);
            if (string.Equals(key, "stable", StringComparison.OrdinalIgnoreCase)) return cachedReleases;
            return LoadCachedReleases(key);
        }

        public async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(string channel, bool force = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            var key = NormalizeChannel(channel);
            var cached = string.Equals(key, "stable", StringComparison.OrdinalIgnoreCase) ? cachedReleases : LoadCachedReleases(key);
            var cachePath = GetCachePath(key);
            var channelCheckedAt = File.Exists(cachePath) ? File.GetLastWriteTimeUtc(cachePath) : DateTime.MinValue;
            if (!force && cached.Count > 0 && DateTime.UtcNow - channelCheckedAt < TimeSpan.FromMinutes(15)) return cached;
            try
            {
                var repository = GetRepository(key);
                using (var response = await client.GetAsync("https://api.github.com/repos/" + repository.Item1 + "/" + repository.Item2 + "/releases?per_page=30", cancellationToken))
                {
                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync();
                    var releases = JsonConvert.DeserializeObject<List<GitHubRelease>>(json) ?? new List<GitHubRelease>();
                    cached = releases.Where(r => !r.draft && (string.Equals(key, "stable", StringComparison.OrdinalIgnoreCase) ? !r.prerelease : true))
                        .Select(r => ToReleaseInfo(r, key)).Where(x => x != null).ToList();
                    if (string.Equals(key, "stable", StringComparison.OrdinalIgnoreCase)) cachedReleases = cached.ToList();
                    File.WriteAllText(GetCachePath(key), JsonConvert.SerializeObject(cached, Formatting.Indented));
                    return cached;
                }
            }
            // HttpClient reports its own timeout as TaskCanceledException; only a caller-requested
            // cancellation should propagate when a cache is available.
            catch (Exception ex) when ((ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)) && cached.Count > 0)
            {
                return cached;
            }
        }

        public async Task<string> DownloadReleaseAsync(ReleaseInfo release, IProgress<double> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (release == null || string.IsNullOrWhiteSpace(release.AssetUrl)) throw new ArgumentException("该版本没有可下载的安装包。", nameof(release));
            var extension = Path.GetExtension(release.AssetName);
            var destination = Path.Combine(cacheDirectory, Sanitize((release.Channel ?? "stable") + "_" + release.TagName) + extension);
            if (File.Exists(destination) && new FileInfo(destination).Length > 0) return destination;

            using (var response = await client.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? -1L;
                var read = 0L;
                var temporary = destination + ".download";
                try
                {
                    using (var input = await response.Content.ReadAsStreamAsync())
                    using (var output = File.Create(temporary))
                    {
                        var buffer = new byte[64 * 1024];
                        int count;
                        while ((count = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                        {
                            await output.WriteAsync(buffer, 0, count, cancellationToken);
                            read += count;
                            if (total > 0) progress?.Report((double)read / total);
                        }
                    }
                    if (File.Exists(destination)) File.Delete(destination);
                    File.Move(temporary, destination);
                }
                catch
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                    throw;
                }
            }
            return destination;
        }

        public void Dispose() => client.Dispose();

        private static ReleaseInfo ToReleaseInfo(GitHubRelease release, string channel)
        {
            var asset = release.assets?.FirstOrDefault(a => a.name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
                        ?? release.assets?.FirstOrDefault(a => a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (asset == null) return null;
            return new ReleaseInfo
            {
                TagName = release.tag_name,
                Name = string.IsNullOrWhiteSpace(release.name) ? release.tag_name : release.name,
                Channel = channel,
                PublishedAt = release.published_at,
                AssetName = asset.name,
                AssetUrl = asset.browser_download_url,
                IsPrerelease = release.prerelease
            };
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "release";
            return new string(value.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_').ToArray());
        }

        private void LoadCachedReleases()
        {
            cachedReleases = LoadCachedReleases("stable").ToList();
        }

        private IReadOnlyList<ReleaseInfo> LoadCachedReleases(string channel)
        {
            var path = GetCachePath(channel);
            if (!File.Exists(path) && string.Equals(NormalizeChannel(channel), "stable", StringComparison.OrdinalIgnoreCase))
            {
                var legacy = Path.Combine(cacheDirectory, "releases.json");
                if (File.Exists(legacy)) path = legacy;
            }
            if (!File.Exists(path)) return new List<ReleaseInfo>();
            try { return JsonConvert.DeserializeObject<List<ReleaseInfo>>(File.ReadAllText(path)) ?? new List<ReleaseInfo>(); }
            catch { return new List<ReleaseInfo>(); }
        }

        private string GetCachePath(string channel) => Path.Combine(cacheDirectory, "releases-" + NormalizeChannel(channel) + ".json");

        private static string NormalizeChannel(string channel)
        {
            if (string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase)) return "beta";
            if (string.Equals(channel, "nightly", StringComparison.OrdinalIgnoreCase)) return "nightly";
            return "stable";
        }

        private static Tuple<string, string> GetRepository(string channel)
        {
            switch (NormalizeChannel(channel))
            {
                case "beta": return Tuple.Create("Optiscaler-Client", "OptiScaler-Betas");
                case "nightly": return Tuple.Create("optiscaler", "OptiScaler-nightly");
                default: return Tuple.Create("optiscaler", "OptiScaler");
            }
        }

        private sealed class GitHubRelease
        {
            public string tag_name { get; set; }
            public string name { get; set; }
            public DateTime published_at { get; set; }
            public bool draft { get; set; }
            public bool prerelease { get; set; }
            public List<GitHubAsset> assets { get; set; }
        }

        private sealed class GitHubAsset
        {
            public string name { get; set; }
            public string browser_download_url { get; set; }
        }
    }
}
