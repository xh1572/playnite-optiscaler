using System;
using System.Collections.Generic;

namespace OptiScaler.Playnite.Core.Models
{
    public sealed class ManagedGame
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string InstallDirectory { get; set; } = string.Empty;
        public string IconPath { get; set; } = string.Empty;
        public string CoverPath { get; set; } = string.Empty;
        public bool IsInstalledInPlaynite { get; set; }
        public bool IsRunning { get; set; }
        public List<string> ExecutablePaths { get; set; } = new List<string>();
    }

    public enum OptiScalerInstallState
    {
        Unknown,
        NotInstalled,
        Installed,
        UpdateAvailable,
        Conflict,
        Incomplete,
        MissingDirectory
    }

    public sealed class OptiScalerStatus
    {
        public OptiScalerInstallState State { get; set; }
        public string Version { get; set; }
        public string InjectionMethod { get; set; }
        public string InstalledDirectory { get; set; }
        public string Message { get; set; }
        public DateTime CheckedAtUtc { get; set; }
        public InstallationManifest Manifest { get; set; }
        public List<string> Components { get; set; } = new List<string>();
        public string ConfigurationPath { get; set; }
        public bool IsManaged => Manifest != null && !string.IsNullOrWhiteSpace(Manifest.OperationId);
        public bool HasFsr4Swap => Manifest != null && Manifest.IncludesFsr4Swap;
        public string Fsr4SwapVersion => Manifest?.Fsr4SwapVersion;
        // A swap-only manifest manages FSR4 DLLs but no OptiScaler install.
        public bool IsSwapOnly => HasFsr4Swap && !Manifest.IncludesOptiScaler;

        public bool IsInstalled => State == OptiScalerInstallState.Installed ||
                                    State == OptiScalerInstallState.UpdateAvailable ||
                                    State == OptiScalerInstallState.Conflict;
    }
}
