using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MingalTunnel.App.Services;

public static class IconCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Frozen, so callable from any thread.</summary>
    public static ImageSource? Get(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached)) return cached;
        }
        ImageSource? img = null;
        try
        {
            if (File.Exists(path))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    img = src;
                }
            }
        }
        catch
        {
            img = null;
        }
        lock (Cache) Cache[path] = img;
        return img;
    }
}
