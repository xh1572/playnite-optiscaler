using System;
using System.Globalization;
using System.Windows.Data;
using OptiScaler.Playnite.Core.Services;

namespace OptiScaler.Playnite.Views
{
    /// <summary>Displays the INI values used by the quick editor in Chinese while preserving raw values for binding.</summary>
    public sealed class ConfigValueConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var release = value as ReleaseInfo;
            if (release != null) return release.Name ?? release.TagName ?? string.Empty;
            var text = value as string;
            if (string.Equals(text, "auto", StringComparison.OrdinalIgnoreCase)) return "自动";
            if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)) return "启用";
            if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)) return "禁用";
            if (string.Equals(text, "nofg", StringComparison.OrdinalIgnoreCase)) return "禁用帧生成";
            if (string.Equals(text, "fsr22", StringComparison.OrdinalIgnoreCase)) return "FSR 2.2";
            if (string.Equals(text, "fsr31", StringComparison.OrdinalIgnoreCase)) return "FSR 3.1";
            if (string.Equals(text, "fsr31_12", StringComparison.OrdinalIgnoreCase)) return "FSR 3.1/4（DX12）";
            if (string.Equals(text, "xess", StringComparison.OrdinalIgnoreCase)) return "XeSS";
            if (string.Equals(text, "xess_12", StringComparison.OrdinalIgnoreCase)) return "XeSS（DX12）";
            if (string.Equals(text, "dlss", StringComparison.OrdinalIgnoreCase)) return "DLSS";
            if (string.Equals(text, "stable", StringComparison.OrdinalIgnoreCase)) return "稳定版";
            if (string.Equals(text, "beta", StringComparison.OrdinalIgnoreCase)) return "Beta 测试版";
            if (string.Equals(text, "nightly", StringComparison.OrdinalIgnoreCase)) return "Nightly 每日版";
            return text ?? string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
