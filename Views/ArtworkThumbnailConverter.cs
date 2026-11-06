using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ProjectionMapper.Views
{
    /// <summary>Decode only the small saved library thumbnail; full images use STA workers.</summary>
    public sealed class ArtworkThumbnailConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string data || string.IsNullOrEmpty(data)) return null;
            try
            {
                using var stream = new MemoryStream(System.Convert.FromBase64String(data));
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
            }
            catch (Exception) { System.Diagnostics.Debug.WriteLine("Artwork thumbnail could not be decoded."); return null; } // The library remains navigable if a thumbnail cannot be decoded.
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
