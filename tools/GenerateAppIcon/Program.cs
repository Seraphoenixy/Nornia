using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Generates the Nornia brand icon (rounded accent tile + white "N") as a multi-size Vista+ PNG
// ICO. Sizes are rendered independently (crisp small glyphs), then packed as PNG-compressed
// frames. Kept as a tool so the brand color can be regenerated later.
//
// Usage (from the repo root):
//   dotnet run --project tools/GenerateAppIcon -- src/Nornia.Desktop/Assets/app.ico

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: GenerateAppIcon <output.ico>");
    return 2;
}

var outPath = Path.GetFullPath(args[0]);
var directory = Path.GetDirectoryName(outPath)!;
Directory.CreateDirectory(directory);

var sizes = new[] { 16, 24, 32, 48, 64, 256 };

var images = new List<byte[]>(sizes.Length);
foreach (var size in sizes)
{
    images.Add(RenderIconPng(size));
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

Console.WriteLine($"Wrote {outPath} ({images.Count} PNG frames: {string.Join("/", sizes)})");
return 0;

static byte[] RenderIconPng(int size)
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

    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var ms = new MemoryStream();
    encoder.Save(ms);
    return ms.ToArray();
}
