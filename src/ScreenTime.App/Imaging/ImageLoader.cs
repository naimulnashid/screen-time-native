using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Svg.Skia;
using Windows.Storage.Streams;

namespace ScreenTime.App.Imaging;

/// <summary>
/// Turns an image file into something XAML can draw.
/// </summary>
/// <remarks>
/// SVG goes through Svg.Skia, rasterised at the size it will be shown times the
/// display scale (times two, for a little supersampling), rather than through
/// WinUI's SvgImageSource. That renderer supports a subset of SVG - no text, no
/// filters, no CSS in a &lt;style&gt; block - and a file it cannot handle
/// simply draws nothing, which is the one failure a logo folder must not have.
/// Rendering on a worker thread also keeps a folder of logos off the UI thread.
/// </remarks>
public static class ImageLoader
{
    /// <summary>PNG bytes for an SVG, fitted and centred in a square.</summary>
    public static byte[]? RenderSvg(string path, int pixels)
    {
        try
        {
            using var svg = new SKSvg();
            var picture = svg.Load(path);
            if (picture is null) return null;
            var bounds = picture.CullRect;
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;
            var scale = pixels / Math.Max(bounds.Width, bounds.Height);
            using var bitmap = new SKBitmap(pixels, pixels, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.Translate((pixels - bounds.Width * scale) / 2, (pixels - bounds.Height * scale) / 2);
                canvas.Scale(scale);
                canvas.Translate(-bounds.Left, -bounds.Top);
                canvas.DrawPicture(picture);
            }
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// An image source for any supported file, or null when it cannot be read.
    /// Call on the UI thread; the decoding work happens off it.
    /// </summary>
    public static async Task<ImageSource?> LoadAsync(string path, double displaySize, double scale)
    {
        try
        {
            byte[]? bytes;
            if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var pixels = (int)Math.Ceiling(displaySize * Math.Max(1, scale) * 2);
                bytes = await Task.Run(() => RenderSvg(path, pixels));
            }
            else
            {
                bytes = await Task.Run(() => File.ReadAllBytes(path));
            }
            if (bytes is null || bytes.Length == 0) return null;

            var bitmap = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
