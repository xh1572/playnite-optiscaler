using System;
using System.Collections.Generic;

namespace OptiScaler.Playnite.Core.Models
{
    public sealed class ManifestFileRecord
    {
        public string RelativePath { get; set; } = string.Empty;
        public string BackupRelativePath { get; set; }
        public bool ExistedBefore { get; set; }
        public string PreInstallSha256 { get; set; }
        public string PostInstallSha256 { get; set; }
    }

    public sealed class InstallationManifest
    {
        public int ManifestVersion { get; set; } = 1;
        public string OperationId { get; set; } = string.Empty;
        public string OperationStatus { get; set; } = "committed";
        public string GameId { get; set; } = string.Empty;
        public string GameInstallDirectory { get; set; } = string.Empty;
        public string InstalledGameDirectory { get; set; } = string.Empty;
        public string OptiScalerVersion { get; set; }
        public string InjectionMethod { get; set; } = string.Empty;
        public string InstallDateUtc { get; set; } = string.Empty;
        public string AppliedProfileName { get; set; }
        public List<string> InstalledFiles { get; set; } = new List<string>();
        public List<string> BackedUpFiles { get; set; } = new List<string>();
        public List<string> InstalledDirectories { get; set; } = new List<string>();
        public List<ManifestFileRecord> FilesCreated { get; set; } = new List<ManifestFileRecord>();
        public List<ManifestFileRecord> FilesOverwritten { get; set; } = new List<ManifestFileRecord>();
        public List<string> ExpectedFinalMarkers { get; set; } = new List<string>();
        public bool IncludesOptiScaler { get; set; } = true;
        public bool IncludesFakenvapi { get; set; }
        public bool IncludesNukemFG { get; set; }
        public bool IncludesOptiPatcher { get; set; }
        public List<string> Components { get; set; } = new List<string>();
        public string Error { get; set; }
        public string MigrationSource { get; set; }
        // Set only while an update is in progress: the committed manifest being replaced and the
        // files snapshotted from it, so a failed update can restore the previous installation.
        public InstallationManifest PreviousManifest { get; set; }
        public List<string> UpdateBackupFiles { get; set; } = new List<string>();
        // FSR4 DLL swap (OptiScaler Extras). Can coexist with an OptiScaler install on the same
        // manifest, or exist alone when IncludesOptiScaler is false (swap-only mode).
        public bool IncludesFsr4Swap { get; set; }
        public string Fsr4SwapVersion { get; set; }
        // Cached package and file selection, used to re-apply the swap after an OptiScaler update.
        public string Fsr4SwapPackagePath { get; set; }
        public string Fsr4SwapScope { get; set; }
        public List<string> Fsr4SwapFiles { get; set; } = new List<string>();
        // Swapped files the manifest already owned (e.g. OptiScaler's own FSR DLL). Their pre-swap
        // bytes live in the backup's fsr4-files scope so a restore returns to the installed copy.
        public List<string> Fsr4PreservedFiles { get; set; } = new List<string>();
    }

    public sealed class Fsr4SwapResult
    {
        public List<string> Files { get; } = new List<string>();
    }

    public sealed class UninstallResult
    {
        public List<string> RestoredFiles { get; } = new List<string>();
        public List<string> RemovedFiles { get; } = new List<string>();
        public List<string> Conflicts { get; } = new List<string>();
        public bool Completed => Conflicts.Count == 0;
    }
}
