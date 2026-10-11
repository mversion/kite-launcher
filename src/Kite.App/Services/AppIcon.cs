using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kite.App.Services;

/// <summary>The app icon (Assets\kite.ico) at a chosen size, for places that show the logo inside a window.</summary>
public static class AppIcon
{
    /// <summary>
    /// The smallest frame of the icon that is at least <paramref name="pixels"/> wide (the .ico holds
    /// 16-256 px versions), so the logo stays sharp instead of being scaled up from 16 px.
    /// </summary>
    public static ImageSource? Frame(int pixels)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/kite.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderBy(f => f.PixelWidth).FirstOrDefault(f => f.PixelWidth >= pixels) ?? decoder.Frames.MaxBy(f => f.PixelWidth);
            frame?.Freeze();
            return frame;
        }
        catch (Exception ex)
        {
            Logger.Log($"App icon couldn't be loaded: {ex.Message}");
            return null;
        }
    }
}
