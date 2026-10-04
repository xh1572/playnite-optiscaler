using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows.Data;

namespace OptiScaler.Playnite.Views
{
    public sealed class IniSettingViewModel : INotifyPropertyChanged
    {
        private static readonly IDictionary<string, string> DisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Dx11Upscaler", "DirectX 11 超分方案" }, { "Dx12Upscaler", "DirectX 12 超分方案" }, { "VulkanUpscaler", "Vulkan 超分方案" },
            { "Enabled", "启用" }, { "FGInput", "帧生成输入" }, { "FGOutput", "帧生成输出" }, { "FTInput", "帧时间来源" },
            { "LoadAsiPlugins", "加载 ASI 插件" }, { "LoadSpecialK", "加载 Special K" }, { "LoadReshade", "加载 ReShade" },
            { "OutputScaling", "输出缩放" }, { "Multiplier", "缩放倍率" }, { "Downscaler", "缩放算法" },
            { "UpscaleRatioOverrideEnabled", "启用超分比例覆盖" }, { "UpscaleRatioOverrideValue", "超分比例值" },
            { "HUDFix", "HUD 修复" }, { "HUDLimit", "HUD 捕获延迟" }, { "HUDFixExtended", "扩展 HUD 修复" },
            { "Fsr4Update", "启用 FSR4 更新" }, { "Fsr4Model", "FSR4 模型" }, { "Fsr4EnableDebugView", "FSR4 调试视图" },
            { "Fsr4EnableWatermark", "FSR4 水印" }, { "Fsr4ForceModel", "强制 FSR4 模型" }, { "Fsr4ForceEnableInt8", "强制启用 INT8" },
            { "Dxgi", "DXGI GPU 伪装" }, { "StreamlineSpoofing", "Streamline 伪装" }, { "Vulkan", "Vulkan GPU 伪装" },
            { "SpoofHAGS", "伪装硬件 GPU 调度" }, { "OverlayMenu", "游戏内菜单" }, { "ShowFps", "显示 FPS" },
            { "ShortcutKey", "菜单快捷键" }, { "FpsOverlayType", "FPS 显示样式" }, { "FpsOverlayPos", "FPS 显示位置" },
            { "FpsScale", "FPS 显示缩放" }, { "Scale", "菜单缩放" }, { "DisableOverlays", "禁用平台覆盖层" },
            { "LogToFile", "写入日志文件" }, { "LogToConsole", "写入控制台" }, { "CheckForUpdate", "检查更新" }
        };
        private static readonly IDictionary<string, string> Tooltips = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Dx11Upscaler", "选择 DX11 游戏使用的超分方案。auto 使用 OptiScaler 默认值；常见选项包括 FSR、XeSS 和 DLSS。" },
            { "Dx12Upscaler", "选择 DX12 游戏使用的超分方案。auto 使用 OptiScaler 默认值；默认配置通常会优先选择 XeSS。" },
            { "VulkanUpscaler", "选择 Vulkan 游戏使用的超分方案。auto 使用 OptiScaler 默认值。" },
            { "Enabled", "启用当前配置节对应的功能。auto 表示交给 OptiScaler 按游戏和硬件决定。" },
            { "FGInput", "选择帧生成输入来源。auto 跟随游戏检测结果；nofg 表示关闭帧生成输入。" },
            { "FGOutput", "选择帧生成输出方式。auto 由 OptiScaler 选择；具体选项需要与游戏和硬件兼容。" },
            { "FTInput", "选择帧时间输入来源，用于帧生成的时间信息处理。auto 使用默认路径。" },
            { "LoadAsiPlugins", "允许 OptiScaler 从 plugins 文件夹加载 .asi 插件。只在确实需要相关插件时启用。" },
            { "OutputScaling", "启用输出缩放功能，可在超分完成后把图像缩放到目标输出分辨率。" },
            { "Multiplier", "输出缩放倍率，支持 0.5 到 3.0。auto 使用默认倍率，通常为 1.5。" },
            { "Downscaler", "输出缩放使用的缩放算法：0 FSR1、1 Bicubic、2 Catmull-Rom、3 Lanczos2、4 Lanczos3、5 Kaiser2、6 Kaiser3、7 MAGIC。" },
            { "UpscaleRatioOverrideEnabled", "启用内部渲染分辨率覆盖。启用后才会使用下方的超分比例值。" },
            { "UpscaleRatioOverrideValue", "强制超分比例值。auto 使用游戏默认值，常见默认值为 1.3。" },
            { "HUDFix", "启用 FSR 3.1 帧生成的 HUD 修复。部分游戏或 Async 路径可能因此崩溃，遇到问题请恢复为 auto。" },
            { "Fsr4Update", "将 FSR 3.x 调用更新为 FSR4。auto 会根据 GPU 决定，RDNA4 通常默认启用。" },
            { "Fsr4Model", "选择 FSR4 模型：0 原生 AA，1 Ultra Quality/Quality，2 Balanced，3 Performance，4 DRS，5 Ultra Performance。" },
            { "Dxgi", "启用 DXGI GPU 伪装，把 GPU 报告为 NVIDIA。AMD/Intel 使用 DLSS-only 游戏时可能需要，auto 会按硬件自动决定。" },
            { "StreamlineSpoofing", "即使关闭 DXGI 伪装，也为 NVIDIA Streamline 启用 GPU 伪装，适用于部分 DLSS/DLSS-G 游戏。" },
            { "SpoofHAGS", "伪装硬件加速 GPU 计划状态。NukemFG 或部分 DLSS-G 场景可能需要，auto 使用 OptiScaler 默认值。" },
            { "Fsr4EnableDebugView", "显示 FSR4 调试视图，可能增加显存占用。" },
            { "Fsr4EnableWatermark", "显示 FSR4 水印，用于确认 FSR4 是否正在工作。" },
            { "Fsr4ForceModel", "强制使用指定 FSR4 模型。0 不覆盖，1 FP8，2 INT8；不要在硬件不支持时强行启用。" },
            { "Fsr4ForceEnableInt8", "强制在驱动未列入白名单的 GPU 上启用 INT8，仅适用于插件随附的 SDK DLL。" },
            { "DisableOverlays", "禁用 Steam/Epic 覆盖层，同时可能禁用 Steam Input。只在覆盖层导致兼容性问题时使用。" },
            { "CheckForUpdate", "允许 OptiScaler 在游戏启动时检查 GitHub 上的最新版本。" }
        };
        private static readonly IReadOnlyList<string> BooleanValues = new[] { "auto", "true", "false" };
        private static readonly IReadOnlyList<string> UpscalerValues = new[] { "auto", "fsr21", "fsr22", "fsr31", "fsr21_12", "fsr22_12", "fsr31_12", "xess", "xess_12", "dlss" };
        private static readonly IReadOnlyList<string> FrameInputValues = new[] { "auto", "nofg", "dlssg", "nvngxfg", "nukems", "fsrfg", "upscaler", "fsrfg30" };
        private static readonly IReadOnlyList<string> FrameOutputValues = new[] { "auto", "nofg", "fsrfg", "xefg", "nvngxfg", "nukems", "dlssg", "dlssgwithnvngx" };
        private static readonly IReadOnlyList<string> DownscalerValues = new[] { "auto", "0", "1", "2", "3", "4", "5", "6", "7" };
        private static readonly IReadOnlyList<string> Fsr4ModelValues = new[] { "auto", "0", "1", "2", "3", "4", "5" };
        private static readonly IReadOnlyList<string> FpsOverlayTypeValues = new[] { "auto", "0", "1", "2", "3", "4", "5", "6" };
        private static readonly IReadOnlyList<string> ScaleValues = new[] { "auto", "0.5", "0.75", "1.0", "1.25", "1.5", "1.75", "2.0" };
        private static readonly IReadOnlyList<string> FpsPositionValues = new[] { "auto", "0", "1", "2", "3" };
        private static readonly IReadOnlyList<string> FpsAlphaValues = new[] { "auto", "0.0", "0.2", "0.4", "0.6", "0.8", "1.0" };
        private static readonly HashSet<string> BooleanKeys = new HashSet<string>(new[]
        {
            "OverlayMenu", "ShowFps", "ExtendedLimits", "UseHQFont", "DisableSplash", "FpsOverlayHorizontal",
            "DebugView", "DrawUIOverFG", "UIPremultipliedAlpha", "DisableHudless", "DisableUI", "SkipReset",
            "VelocityValidNow", "HudlessValidNow", "OnlyAcceptFirstHudless", "PreserveSwapChain", "SkipResizeBuffers", "ModifyBufferState", "ModifySCIndex",
            "LoadAsiPlugins", "LoadSpecialK", "LoadReshade", "LoadCustomAmdxc64OnRdna2", "LogToFile", "LogToConsole", "LogToNGX", "LogToDebug", "OpenConsole", "SingleFile", "LogAsync",
            "DebugTearLines", "DebugResetLines", "DebugPacingLines", "AllowAsync", "UseMutexForSwapchain", "FramePacingTuning", "FPTHybridSpin", "FPTWaitForSingleObjectOnFence", "EnableWatermark",
            "IgnoreInitChecks", "DepthInverted", "UIComposition", "JitteredMV", "HighResMV", "ForceBorderless", "HUDFix", "HUDFixExtended", "HudfixDisableRTV", "HudfixDisableSRV", "HudfixDisableUAV", "HudfixDisableOM", "HudfixDisableSCR", "HudfixDisableSGR", "HudfixDisableDI", "HudfixDisableDII", "HudfixDisableDispatch", "HUDFixDontUseSwapchainBuffers", "HUDFixRelaxedResolutionCheck", "HUDFixImmediate", "AlwaysTrackHeaps", "UseShards", "ResourceBlocking", "MakeDepthCopy", "EnableDepthScale", "MakeMVCopy", "ResourceFlip", "ResourceFlipOffset", "AlwaysCaptureFSRFGSwapchain",
            "EnableDlssInputs", "EnableXeSSInputs", "EnableFsr2Inputs", "UseFsr2Dx11Inputs", "UseFsr2VulkanInputs", "UseFsr2Inputs", "Fsr2Pattern", "EnableFsr3Inputs", "UseFsr3Inputs", "Fsr3Pattern", "EnableFfxInputs", "UseFfxInputs", "EnableHotSwapping", "SkipConfigForHudless", "SkipDispatchForHudless",
            "BuildPipelines", "CreateHeaps", "UseFsrInputValues", "Fsr4Update", "Fsr4EnableDebugView", "Fsr4EnableWatermark", "FsrNonLinearColorSpace", "FsrNonLinearSRGB", "FsrNonLinearPQ", "FsrAgilitySDKUpgrade", "Fsr4ForceEnableInt8",
            "Enabled", "RenderPresetOverride", "UseGenericAppIdWithDlss", "StreamlineSpoofing", "Dxgi", "DxgiFactoryWrapping", "Vulkan", "VulkanExtensionSpoofing", "SpoofHAGS", "D3DFeatureLevel", "UEIntelAtomics", "Registry", "User32",
            "OverrideNvapiDll", "DisableFlipMetering", "DontUseFakenvapiForXeLLOnNvidia", "UseDelayedInit", "DontUseNTShared", "EarlyHooking", "HookOriginalNvngxOnly", "UseNtdllHooks", "OverrideSharpness", "MotionSharpnessEnabled", "MotionSharpnessDebug", "ContrastEnabled",
            "AutoExposure", "HDR", "JitterCancellation", "DisplayResolution", "DisableReactiveMask", "UpscaleRatioOverrideEnabled", "QualityRatioOverrideEnabled", "DrsMinOverrideEnabled", "DrsMaxOverrideEnabled", "ForceHDR", "UseHDR10", "SkipColorSpace", "OverrideVsync", "ForceVsync", "ModifyComparison", "ModifyMinMax", "SkipPointFilter", "MipmapBiasFixedOverride", "MipmapBiasScaleOverride", "MipmapBiasOverrideAll",
            "CheckForUpdate", "DisableOverlays", "DontCreateD3D12DeviceForLuma", "PreferDedicatedGpu", "PreferFirstDedicatedGpu", "RestoreComputeSignature", "RestoreGraphicSignature", "UsePrecompiledShaders"
        }, StringComparer.OrdinalIgnoreCase);
        private string value;

        public string Key { get; private set; }
        public string DisplayName => DisplayNames.TryGetValue(Key, out var displayName) ? displayName : Key;
        public string ToolTip => Tooltips.TryGetValue(Key, out var tooltip)
            ? tooltip
            : "OptiScaler.ini 配置项：" + Key + "。auto 表示使用 OptiScaler 默认行为。";
        public IReadOnlyList<string> Options { get; private set; }
        public bool HasOptions => Options != null && Options.Count > 0;
        public string Value
        {
            get => value;
            set
            {
                if (string.Equals(this.value, value, StringComparison.Ordinal)) return;
                this.value = value ?? string.Empty;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }

        public IniSettingViewModel(string key, string value)
        {
            Key = key;
            this.value = value ?? string.Empty;
            Options = GetOptions(key);
        }

        private static IReadOnlyList<string> GetOptions(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            if (string.Equals(key, "Dx11Upscaler", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "Dx12Upscaler", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "VulkanUpscaler", StringComparison.OrdinalIgnoreCase)) return UpscalerValues;
            if (string.Equals(key, "FGInput", StringComparison.OrdinalIgnoreCase)) return FrameInputValues;
            if (string.Equals(key, "FGOutput", StringComparison.OrdinalIgnoreCase)) return FrameOutputValues;
            if (string.Equals(key, "Downscaler", StringComparison.OrdinalIgnoreCase)) return DownscalerValues;
            if (string.Equals(key, "Fsr4Model", StringComparison.OrdinalIgnoreCase)) return Fsr4ModelValues;
            if (string.Equals(key, "FpsOverlayType", StringComparison.OrdinalIgnoreCase)) return FpsOverlayTypeValues;
            if (string.Equals(key, "Scale", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "FpsScale", StringComparison.OrdinalIgnoreCase)) return ScaleValues;
            if (string.Equals(key, "FpsOverlayPos", StringComparison.OrdinalIgnoreCase)) return FpsPositionValues;
            if (string.Equals(key, "FpsOverlayAlpha", StringComparison.OrdinalIgnoreCase)) return FpsAlphaValues;
            if (string.Equals(key, "Enabled", StringComparison.OrdinalIgnoreCase) ||
                key.EndsWith("Enabled", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("HUD", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "HUDFix", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "Dxgi", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "Vulkan", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "StreamlineSpoofing", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "Fsr4Update", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "LoadAsiPlugins", StringComparison.OrdinalIgnoreCase) ||
                BooleanKeys.Contains(key)) return BooleanValues;
            return null;
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public sealed class IniSectionViewModel
    {
        private static readonly IDictionary<string, string> SectionDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Upscalers", "超分方案" }, { "Menu", "游戏内菜单" }, { "FrameGen", "帧生成" }, { "Plugins", "插件" },
            { "Log", "日志" }, { "FSRFG", "FSR 帧生成" }, { "FramePaceTuning", "帧时间调整" }, { "XeFG", "Xe 帧生成" },
            { "OptiFG", "OptiFG / HUD" }, { "Inputs", "输入识别" }, { "FSRFGInputs", "FSR 帧生成输入" }, { "Framerate", "帧率" },
            { "XeSS", "XeSS" }, { "FSR", "FSR" }, { "DLSS", "DLSS" }, { "DLSSD", "DLSS 细节" }, { "Nukems", "NukemFG" },
            { "Spoofing", "GPU 伪装" }, { "NvApi", "NVAPI" }, { "Dx11withDx12", "DX11 转 DX12" }, { "Hooks", "挂钩" },
            { "Sharpness", "锐化" }, { "OutputScaling", "输出缩放" }, { "CAS", "CAS 锐化" }, { "InitFlags", "初始化标记" },
            { "UpscaleRatio", "超分比例" }, { "QualityOverrides", "质量覆盖" }, { "DRS", "动态分辨率" }, { "HDR", "HDR" },
            { "V-Sync", "垂直同步" }, { "Anisotropy", "各向异性过滤" }, { "Mipmap", "Mipmap" }, { "ProcessFilter", "进程过滤" },
            { "Hotfix", "兼容性修复" }
        };
        private string filterText = string.Empty;

        public string Name { get; private set; }
        public string DisplayName => SectionDisplayNames.TryGetValue(Name, out var displayName) ? displayName : Name;
        public ObservableCollection<IniSettingViewModel> Settings { get; } = new ObservableCollection<IniSettingViewModel>();
        public ICollectionView SettingsView { get; }

        public IniSectionViewModel(string name)
        {
            Name = name;
            SettingsView = CollectionViewSource.GetDefaultView(Settings);
            SettingsView.Filter = FilterSetting;
        }

        public void SetFilter(string text)
        {
            filterText = text ?? string.Empty;
            SettingsView.Refresh();
        }

        private bool FilterSetting(object value)
        {
            var setting = value as IniSettingViewModel;
            if (setting == null) return false;
            if (string.IsNullOrWhiteSpace(filterText)) return true;
            return setting.Key.IndexOf(filterText, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                   setting.Value.IndexOf(filterText, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }
    }

    public sealed class IniDocumentViewModel : INotifyPropertyChanged
    {
        private readonly List<IniLine> lines;
        private readonly bool trailingNewLine;
        private string searchText = string.Empty;

        public ObservableCollection<IniSectionViewModel> Sections { get; } = new ObservableCollection<IniSectionViewModel>();
        public string SearchText
        {
            get => searchText;
            set
            {
                searchText = value ?? string.Empty;
                foreach (var section in Sections) section.SetFilter(searchText);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchText)));
            }
        }

        private IniDocumentViewModel(List<IniLine> lines, bool trailingNewLine)
        {
            this.lines = lines;
            this.trailingNewLine = trailingNewLine;
        }

        public static IniDocumentViewModel Parse(string text)
        {
            text = text ?? string.Empty;
            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var rawLines = normalized.Split(new[] { '\n' }, StringSplitOptions.None);
            var trailing = normalized.EndsWith("\n", StringComparison.Ordinal);
            if (trailing && rawLines.Length > 0) rawLines = rawLines.Take(rawLines.Length - 1).ToArray();
            var lines = new List<IniLine>();
            var document = new IniDocumentViewModel(lines, trailing);
            IniSectionViewModel current = null;
            foreach (var raw in rawLines)
            {
                var line = new IniLine { Raw = raw };
                var trimmed = raw.Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal) && trimmed.Length > 2)
                {
                    current = new IniSectionViewModel(trimmed.Substring(1, trimmed.Length - 2).Trim());
                    document.Sections.Add(current);
                    line.Section = current;
                }
                else
                {
                    line.Section = current;
                    if (current != null && !trimmed.StartsWith(";", StringComparison.Ordinal) && !trimmed.StartsWith("#", StringComparison.Ordinal))
                    {
                        var separator = raw.IndexOf('=');
                        if (separator > 0)
                        {
                            var key = raw.Substring(0, separator).Trim();
                            var setting = new IniSettingViewModel(key, raw.Substring(separator + 1).Trim());
                            current.Settings.Add(setting);
                            line.Setting = setting;
                        }
                    }
                }
                lines.Add(line);
            }
            return document;
        }

        public string GetValue(string sectionName, string key, string fallback = "auto")
        {
            var section = Sections.FirstOrDefault(x => string.Equals(x.Name, sectionName, StringComparison.OrdinalIgnoreCase));
            var setting = section?.Settings.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            return setting == null ? fallback : setting.Value;
        }

        public void SetValue(string sectionName, string key, string value)
        {
            var section = Sections.FirstOrDefault(x => string.Equals(x.Name, sectionName, StringComparison.OrdinalIgnoreCase));
            if (section == null)
            {
                section = new IniSectionViewModel(sectionName);
                Sections.Add(section);
                if (lines.Count > 0 && !string.IsNullOrEmpty(lines[lines.Count - 1].Raw)) lines.Add(new IniLine { Raw = string.Empty });
                lines.Add(new IniLine { Raw = "[" + sectionName + "]", Section = section });
            }

            var setting = section.Settings.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (setting != null)
            {
                setting.Value = value ?? string.Empty;
                return;
            }

            setting = new IniSettingViewModel(key, value);
            section.Settings.Add(setting);
            var lastLine = lines.FindLastIndex(x => ReferenceEquals(x.Section, section));
            if (lastLine < 0) lastLine = lines.Count - 1;
            lines.Insert(lastLine + 1, new IniLine { Raw = key + "=" + (value ?? string.Empty), Section = section, Setting = setting });
        }

        public string ToText()
        {
            var builder = new StringBuilder();
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.Setting != null)
                {
                    var keyStart = line.Raw.IndexOf('=');
                    var key = keyStart > 0 ? line.Raw.Substring(0, keyStart).Trim() : line.Setting.Key;
                    line.Raw = key + "=" + line.Setting.Value;
                }
                builder.Append(line.Raw);
                if (i < lines.Count - 1 || trailingNewLine) builder.Append(Environment.NewLine);
            }
            return builder.ToString();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private sealed class IniLine
        {
            public string Raw { get; set; }
            public IniSectionViewModel Section { get; set; }
            public IniSettingViewModel Setting { get; set; }
        }
    }
}
