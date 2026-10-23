using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Quaykeep.Services;

/// <summary>
/// The app icon: a blue rounded tile with a white terminal prompt and a green status dot (grey when locked).
/// The same drawing produces the installer icons in Assets (app.ico, app.png, app.icns: see <see cref="WriteAll"/>)
/// and the runtime tray / window icons.
/// </summary>
public static class IconFactory
{
    public static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    public static RenderTargetBitmap Render(int size, bool unlocked)
    {
        var bmp = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using var g = bmp.CreateDrawingContext();
        double s = size;

        // tile
        var inset = s <= 20 ? 0.5 : s * 0.04;
        var tile = new Rect(inset, inset, s - 2 * inset, s - 2 * inset);
        var radius = s * 0.23;
        var (from, to) = unlocked ? (Color.FromRgb(60, 146, 255), Color.FromRgb(24, 72, 214)) : (Color.FromRgb(150, 155, 164), Color.FromRgb(88, 94, 104));
        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(s, s, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) },
        };
        g.DrawRectangle(fill, null, tile, radius, radius);
        if (size >= 32)
        {
            // soft glass highlight on the upper half
            using (g.PushClip(new RoundedRect(tile, radius)))
            {
                var gloss = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
                    EndPoint = new RelativePoint(0, s * 0.55, RelativeUnit.Absolute),
                    GradientStops = { new GradientStop(Color.FromArgb(60, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) },
                };
                g.DrawRectangle(gloss, null, new Rect(0, 0, s, s * 0.55));
            }
        }

        // prompt ">_"
        var pen = new Pen(Brushes.White, Math.Max(1.6, s * 0.095)) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var chevron = new StreamGeometry();
        using (var c = chevron.Open())
        {
            c.BeginFigure(P(s, 0.25, 0.36), false);
            c.LineTo(P(s, 0.45, 0.54));
            c.LineTo(P(s, 0.25, 0.72));
            c.EndFigure(false);
        }
        g.DrawGeometry(null, pen, chevron);
        g.DrawLine(pen, P(s, 0.54, 0.72), P(s, 0.76, 0.72));

        // status dot (top right)
        if (size >= 20)
        {
            var r = s * 0.12;
            var center = P(s, 0.73, 0.29);
            var rr = r + Math.Max(1, s * 0.035);
            g.DrawEllipse(new SolidColorBrush(unlocked ? Color.FromRgb(24, 72, 214) : Color.FromRgb(88, 94, 104)), null, center, rr, rr);
            g.DrawEllipse(new SolidColorBrush(unlocked ? Color.FromRgb(52, 211, 153) : Color.FromRgb(200, 204, 210)), null, center, r, r);
        }
        return bmp;
    }

    private static Point P(double s, double x, double y) => new(s * x, s * y);

    /// <summary>Tray and window icon. Unlocked uses the multi-size app.ico (crisp in the taskbar); locked is drawn grey.</summary>
    public static WindowIcon Create(bool unlocked, int size = 64)
    {
        if (unlocked)
        {
            try
            {
                return new WindowIcon(AssetLoader.Open(new Uri("avares://Quaykeep/Assets/app.ico")));
            }
            catch (Exception ex) when (ex is IOException or FileNotFoundException)
            {
            }
        }
        return new WindowIcon(Png(Render(size, unlocked)));
    }

    private static MemoryStream Png(Bitmap bitmap)
    {
        var ms = new MemoryStream();
        bitmap.Save(ms, PngBitmapEncoderOptions.Default);
        ms.Position = 0;
        return ms;
    }

    /// <summary>app.ico (Windows), app.png (Linux, 512 px), app.icns (macOS) into <paramref name="dir"/>.</summary>
    public static void WriteAll(string dir)
    {
        Directory.CreateDirectory(dir);
        WriteIco(Path.Combine(dir, "app.ico"));
        using (var png = Render(512, true)) png.Save(Path.Combine(dir, "app.png"), PngBitmapEncoderOptions.Default);
        WriteIcns(Path.Combine(dir, "app.icns"));
    }

    /// <summary>Writes a multi-resolution .ico with PNG entries (Windows Vista and later read them in every size).</summary>
    public static void WriteIco(string path, bool unlocked = true)
    {
        var images = IcoSizes.Select(size =>
        {
            using var bmp = Render(size, unlocked);
            return Png(bmp).ToArray();
        }).ToList();

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)images.Count);
        var offset = 6 + 16 * images.Count;
        for (int i = 0; i < images.Count; i++)
        {
            var size = IcoSizes[i];
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }
        foreach (var img in images) w.Write(img);
    }

    /// <summary>
    /// Writes a macOS .icns: the "icns" header and PNG entries icp4 (16), icp5 (32), ic07 (128), ic08 (256), ic09 (512),
    /// ic10 (1024); every length is big-endian and counts its own 8-byte header.
    /// </summary>
    public static void WriteIcns(string path)
    {
        var entries = new (string Type, int Size)[] { ("icp4", 16), ("icp5", 32), ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024) }
            .Select(e =>
            {
                using var bmp = Render(e.Size, true);
                return (e.Type, Data: Png(bmp).ToArray());
            }).ToList();
        using var fs = File.Create(path);
        void Int(int v) => fs.Write([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        fs.Write("icns"u8);
        Int(8 + entries.Sum(e => 8 + e.Data.Length));
        foreach (var (type, data) in entries)
        {
            fs.Write(System.Text.Encoding.ASCII.GetBytes(type));
            Int(8 + data.Length);
            fs.Write(data);
        }
    }
}
