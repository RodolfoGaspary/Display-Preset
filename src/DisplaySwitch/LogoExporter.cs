using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace DisplaySwitch;

/// <summary>Regenerates the logo assets. The artwork is code, so the .ico in the repo can always
/// be rebuilt: <c>DisplaySwitch.exe icon &lt;folder&gt;</c>.</summary>
internal static class LogoExporter
{
    public static IReadOnlyList<string> Export(string folder)
    {
        Directory.CreateDirectory(folder);
        var written = new List<string>();

        var icoPath = Path.Combine(folder, "app.ico");
        IcoWriter.Write(icoPath, Logo.RenderTile);
        written.Add(icoPath);

        var pngPath = Path.Combine(folder, "logo-512.png");
        using (var big = Logo.RenderTile(512))
        {
            big.Save(pngPath, ImageFormat.Png);
        }
        written.Add(pngPath);

        var previewPath = Path.Combine(folder, "logo-preview.png");
        using (var sheet = RenderPreviewSheet())
        {
            sheet.Save(previewPath, ImageFormat.Png);
        }
        written.Add(previewPath);

        return written;
    }

    /// <summary>A contact sheet of every size on both a light and a dark backdrop, plus the tray
    /// icon variants -- the only honest way to check that the small sizes still read.</summary>
    private static Bitmap RenderPreviewSheet()
    {
        int[] sizes = { 256, 128, 64, 48, 32, 24, 20, 16 };
        const int pad = 24;
        const int rowHeight = 300;

        var width = pad * 2 + sizes.Sum(s => s + pad);
        var sheet = new Bitmap(width, rowHeight * 3, PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(sheet);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var light = new SolidBrush(Color.FromArgb(0xF4, 0xF4, 0xF6)))
        using (var dark = new SolidBrush(Color.FromArgb(0x1B, 0x1B, 0x1F)))
        {
            g.FillRectangle(light, 0, 0, width, rowHeight);
            g.FillRectangle(dark, 0, rowHeight, width, rowHeight);
            g.FillRectangle(dark, 0, rowHeight * 2, width, rowHeight);
        }

        DrawRow(g, sizes, 0, size => Logo.RenderTile(size));
        DrawRow(g, sizes, rowHeight, size => Logo.RenderTile(size));

        // Third row: what the tray actually shows.
        var trayLabels = new string?[] { "P", "T", "PC", "TV", null };
        var x = pad;
        var baseline = rowHeight * 2 + rowHeight / 2;
        foreach (var label in trayLabels)
        {
            using var icon = IconFactory.BuildTray(label);
            foreach (var size in new[] { 48, 32, 24, 16 })
            {
                using var bmp = icon.ToBitmap();
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(bmp, new Rectangle(x, baseline - size / 2, size, size));
                x += size + 10;
            }
            x += pad;
        }

        return sheet;
    }

    private static void DrawRow(Graphics g, int[] sizes, int top, Func<int, Bitmap> render)
    {
        var x = 24;
        var baseline = top + rowCentre();
        foreach (var size in sizes)
        {
            using var bmp = render(size);
            g.DrawImage(bmp, new Rectangle(x, baseline - size / 2, size, size));
            x += size + 24;
        }

        static int rowCentre() => 150;
    }
}
