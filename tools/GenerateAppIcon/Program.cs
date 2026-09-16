using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Generates the Nornia brand icon as a multi-size ICO. With a source PNG, the supplied
// artwork is downscaled for each frame; otherwise the legacy N tile is rendered.
//
// IMPORTANT: frames are written as classic uncompressed 32bpp BGRA DIBs (not PNG blobs).
// The .NET apphost icon patcher (Microsoft.NET.HostModel HostWriter/IconConverter) only
// understands BMP DIB frames; a Vista+ PNG-compressed ICO is silently ignored, which leaves
// the published Release .exe with a blank/incorrect Explorer icon. WPF still renders the icon
// fine either way, so the window icon looked correct while the file icon did not.
//
// Usage (from the repo root):
//   dotnet run --project tools/GenerateAppIcon -- src/Nornia.Desktop/Assets/app.ico
//   dotnet run --project tools/GenerateAppIcon -- outputs/icons/nornia-icon.png src/Nornia.Desktop/Assets/app.ico

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: GenerateAppIcon [source.png] <output.ico>");
    return 2;
}

var sourcePath = args.Length == 2 ? Path.GetFullPath(args[0]) : null;
var outPath = Path.GetFullPath(args[^1]);
if (sourcePath is not null && !File.Exists(sourcePath))
{
    Console.Error.WriteLine($"Source image not found: {sourcePath}");
    return 2;
}

var directory = Path.GetDirectoryName(outPath)!;
Directory.CreateDirectory(directory);

var sizes = new[] { 16, 24, 32, 48, 64, 256 };

// Each entry is a classic BMP DIB (BITMAPINFOHEADER + bottom-up BGRA XOR + 1bpp AND mask),
// which the apphost icon patcher can embed into the native .exe.
var images = new List<byte[]>(sizes.Length);
foreach (var size in sizes)
{
    var frame = sourcePath is null ? RenderIcon(size) : ResizeIcon(sourcePath, size);
    images.Add(BitmapSourceToIconImage(frame));
}

using (var w = new BinaryWriter(new FileStream(outPath, FileMode.Create)))
{
    // ICONDIR
    w.Write((ushort)0); // reserved
    w.Write((ushort)1); // type: icon
    w.Write((ushort)sizes.Length);

    uint offset = 6u + 16u * (uint)sizes.Length;
    for (var i = 0; i < sizes.Length; i++)
    {
        var encoded = sizes[i] >= 256 ? (byte)0 : (byte)sizes[i];
        w.Write(encoded); // width
        w.Write(encoded); // height
        w.Write((byte)0); // palette size
        w.Write((byte)0); // reserved
        w.Write((ushort)1); // color planes
        w.Write((ushort)32); // bits per pixel
        w.Write((uint)images[i].Length);
        w.Write(offset);
        offset += (uint)images[i].Length;
    }

    foreach (var image in images)
    {
        w.Write(image);
    }
}

Console.WriteLine($"Wrote {outPath} ({images.Count} BMP-DIB frames: {string.Join("/", sizes)})");
return 0;

// Converts a rendered BitmapSource into a single ICO image: a 32bpp BGRA device-independent
// bitmap (bottom-up pixel rows) followed by a fully-transparent 1bpp AND mask. This is the
// exact layout System.Drawing.Icon emits and the only layout the apphost patcher accepts.
static byte[] BitmapSourceToIconImage(BitmapSource source)
{
    var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
    bgra.Freeze();

    var width = bgra.PixelWidth;
    var height = bgra.PixelHeight;
    var stride = width * 4;
    var pixels = new byte[height * stride];
    bgra.CopyPixels(pixels, stride, 0);

    // XOR bitmap: bottom-up scan order, BGRA per pixel.
    var xor = new byte[height * stride];
    for (var y = 0; y < height; y++)
    {
        var srcRow = (height - 1 - y) * stride; // flip vertically
        var dstRow = y * stride;
        for (var x = 0; x < width; x++)
        {
            var s = srcRow + x * 4;
            var d = dstRow + x * 4;
            xor[d] = pixels[s];         // B
            xor[d + 1] = pixels[s + 1]; // G
            xor[d + 2] = pixels[s + 2]; // R
            xor[d + 3] = pixels[s + 3]; // A
        }
    }

    // AND mask: 1bpp, bottom-up, row-padded to a 4-byte boundary. All zeros (fully opaque).
    var andRowBytes = ((width + 31) / 32) * 4;
    var and = new byte[height * andRowBytes];

    using var dib = new MemoryStream();
    using var w = new BinaryWriter(dib);
    w.Write((uint)40);            // biSize (BITMAPINFOHEADER)
    w.Write((int)width);           // biWidth
    w.Write((int)(height * 2));    // biHeight (XOR + AND)
    w.Write((ushort)1);            // biPlanes
    w.Write((ushort)32);           // biBitCount
    w.Write((uint)0);              // biCompression (BI_RGB)
    w.Write((uint)0);              // biSizeImage
    w.Write((int)0);               // biXPelsPerMeter
    w.Write((int)0);               // biYPelsPerMeter
    w.Write((uint)0);              // biClrUsed
    w.Write((uint)0);              // biClrImportant
    w.Write(xor);
    w.Write(and);
    return dib.ToArray();
}

static BitmapSource RenderIcon(int size)
{
    var radius = size * 0.22;
    var fontPx = size * 0.60;

    var brush = new LinearGradientBrush();
    brush.StartPoint = new Point(0, 0);
    brush.EndPoint = new Point(0, 1);
    brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x2D, 0x9D, 0xFF), 0.0));
    brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x00, 0x5A, 0x9E), 1.0));

    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        dc.DrawRoundedRectangle(brush, null, new Rect(0, 0, size, size), radius, radius);

        var text = new FormattedText(
            "N",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            fontPx,
            Brushes.White,
            VisualTreeHelper.GetDpi(visual).PixelsPerDip);
        var tx = (size - text.Width) / 2.0;
        var ty = (size - text.Height) / 2.0;
        dc.DrawText(text, new Point(tx, ty));
    }

    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    bitmap.Freeze();
    return bitmap;
}

static BitmapSource ResizeIcon(string sourcePath, int size)
{
    var source = new BitmapImage();
    source.BeginInit();
    source.UriSource = new Uri(sourcePath);
    source.CacheOption = BitmapCacheOption.OnLoad;
    source.EndInit();
    source.Freeze();

    var scale = Math.Min((double)size / source.PixelWidth, (double)size / source.PixelHeight);
    var resized = new TransformedBitmap(source, new ScaleTransform(scale, scale));
    resized.Freeze();

    var canvas = new DrawingVisual();
    using (var dc = canvas.RenderOpen())
    {
        var x = (size - resized.PixelWidth) / 2.0;
        var y = (size - resized.PixelHeight) / 2.0;
        dc.DrawImage(resized, new Rect(x, y, resized.PixelWidth, resized.PixelHeight));
    }

    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(canvas);
    bitmap.Freeze();
    return bitmap;
}
