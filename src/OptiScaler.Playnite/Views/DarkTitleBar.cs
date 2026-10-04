using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OptiScaler.Playnite.Views
{
    /// <summary>Gives plugin windows a dark native title bar on Windows 10 1809+ and Windows 11.</summary>
    public static class DarkTitleBar
    {
        private const int UseImmersiveDarkMode = 20;
        private const int UseImmersiveDarkModeLegacy = 19;
        private const int CaptionColor = 35;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public static void Attach(Window window)
        {
            if (window == null) return;
            window.SourceInitialized += (sender, args) => Apply((Window)sender);
        }

        private static void Apply(Window window)
        {
            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;
                var enabled = 1;
                if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                    DwmSetWindowAttribute(handle, UseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
                // COLORREF is 0x00BBGGRR; match the window background (#0E1424). Ignored before Windows 11.
                var caption = 0x0024140E;
                DwmSetWindowAttribute(handle, CaptionColor, ref caption, sizeof(int));
            }
            catch
            {
            }
        }
    }
}
