using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using OptiScaler.Playnite.Core.Models;
using OptiScaler.Playnite.Core.Services;

namespace OptiScaler.Playnite.Playnite
{
    public sealed class PlayniteGameEntry
    {
        public Game PlayniteGame { get; set; }
        public ManagedGame ManagedGame { get; set; }
        public OptiScalerStatus Status { get; set; }
    }

    public sealed class PlayniteGameCatalog
    {
        private readonly IPlayniteAPI api;
        private readonly OptiScalerAnalyzer analyzer;

        public PlayniteGameCatalog(IPlayniteAPI api, string pluginDataPath)
        {
            this.api = api;
            analyzer = new OptiScalerAnalyzer(pluginDataPath);
        }

        public List<PlayniteGameEntry> ReadGames(bool showOnlyInstalled, bool analyzeStatus)
        {
            var result = new List<PlayniteGameEntry>();
            if (api.Database == null || !api.Database.IsOpen) return result;
            foreach (var game in api.Database.Games)
            {
                if (showOnlyInstalled && !game.IsInstalled) continue;
                if (game.Hidden) continue;
                var managed = ToManagedGame(game, analyzeStatus);
                var status = analyzeStatus ? analyzer.Analyze(managed) : null;
                result.Add(new PlayniteGameEntry { PlayniteGame = game, ManagedGame = managed, Status = status });
            }
            return result.OrderBy(x => x.ManagedGame.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public OptiScalerStatus Analyze(ManagedGame game) => analyzer.Analyze(game);

        private ManagedGame ToManagedGame(Game game, bool includeExecutablePaths)
        {
            var managed = new ManagedGame
            {
                Id = game.Id,
                Name = game.Name ?? string.Empty,
                InstallDirectory = game.InstallDirectory ?? string.Empty,
                IconPath = ResolveDatabaseFile(game.Icon),
                CoverPath = ResolveDatabaseFile(game.CoverImage),
                IsInstalledInPlaynite = game.IsInstalled,
                IsRunning = game.IsRunning || game.IsLaunching
            };

            if (!includeExecutablePaths) return managed;

            foreach (var action in game.GameActions ?? new System.Collections.ObjectModel.ObservableCollection<GameAction>())
            {
                if (action.Type != GameActionType.File || string.IsNullOrWhiteSpace(action.Path)) continue;
                try
                {
                    var expanded = api.ExpandGameVariables(game, action);
                    var path = expanded == null ? action.Path : expanded.Path;
                    if (!Path.IsPathRooted(path)) path = Path.Combine(managed.InstallDirectory, path);
                    if (File.Exists(path)) managed.ExecutablePaths.Add(Path.GetFullPath(path));
                }
                catch { }
            }
            return managed;
        }

        private string ResolveDatabaseFile(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath)) return string.Empty;
            try { return api.Database.GetFullFilePath(databasePath); }
            catch { return databasePath; }
        }
    }
}
