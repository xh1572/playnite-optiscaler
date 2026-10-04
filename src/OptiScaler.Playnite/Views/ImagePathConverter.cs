using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OptiScaler.Playnite.Views
{
    /// <summary>Loads Playnite icon files as small frozen bitmaps; missing or unreadable files yield null.</summary>
    public sealed class ImagePathConverter : IValueConverter
    {
        private const int MaxCachedImages = 512;
        private static readonly ConcurrentDictionary<string, ImageSource> Cache = new ConcurrentDictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var path = value as string;
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (Cache.TryGetValue(path, out var cached)) return cached;
            var image = Load(path);
            if (Cache.Count >= MaxCachedImages) Cache.Clear();
            Cache[path] = image;
            return image;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

        private static ImageSource Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.DecodePixelWidth = 96;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }
    }
}
