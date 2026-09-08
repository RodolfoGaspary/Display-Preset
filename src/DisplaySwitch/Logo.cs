using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace DisplaySwitch;

/// <summary>
/// The DisplaySwitch mark: two overlapping screens -- a wide one behind, a smaller one in front,
/// separated by a knocked-out gap so the depth reads even at 16 px.
///
/// Everything is laid out in a 100x100 design space and scaled, and every icon is rendered
/// supersampled then downscaled, which is what keeps the small sizes clean.
/// </summary>
internal static class Logo
{
    public static readonly Color GradientStart = Color.FromArgb(0x4C, 0x6F, 0xFF); // indigo
    public static readonly Color GradientEnd = Color.FromArgb(0x9B, 0x4D, 0xFF);   // violet
    public static readonly Color Inactive = Color.FromArgb(0x8A, 0x8A, 0x94);      // muted grey

    // Design-space geometry (100x100).
    private static readonly RectangleF BackScreen = new(12f, 24f, 60f, 37.5f);
    private static readonly RectangleF FrontScreen = new(44f, 47f, 44f, 28.5f);
    private const float BackRadius = 5.5f;
    private const float FrontRadius = 5f;
    private const float GapWidth = 7.5f;
    private const float TileRadius = 22f;

    /// <summary>The full app icon: gradient tile with the white mark on top.</summary>
    public static Bitmap RenderTile(int size) => Supersample(size, (g, scale) =>
    {
        var edge = 100f * scale;
        using (var path = RoundedRect(new RectangleF(0, 0, edge, edge), TileRadius * scale))
        using (var brush = new LinearGradientBrush(
            new RectangleF(0, 0, edge, edge), GradientStart, GradientEnd, 55f))
        {
            g.FillPath(brush, path);
        }

        Compose(g, edge, Color.White);
    });

    /// <summary>The mark alone on transparency, for flat contexts like the tray.</summary>
    public static Bitmap RenderMark(int size, Color color) => Supersample(size, (g, scale) =>
        Compose(g, 100f * scale, color));

    /// <summary>Draws the mark on its own transparent layer before compositing it. The gap
    /// between the two screens is punched out of that layer only -- punching it straight onto
    /// the tile would cut a hole through the tile as well.</summary>
    private static void Compose(Graphics g, float edge, Color color)
    {
        var side = Math.Max(1, (int)Math.Round(edge));
        using var layer = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using (var lg = Graphics.FromImage(layer))
        {
            lg.SmoothingMode = SmoothingMode.AntiAlias;
            lg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            lg.Clear(Color.Transparent);
            DrawMark(lg, side / 100f, color);
        }
        g.DrawImage(layer, new Rectangle(0, 0, side, side));
    }

    private static void DrawMark(Graphics g, float scale, Color color)
    {
        using var brush = new SolidBrush(color);

        using (var back = RoundedRect(Scale(BackScreen, scale), BackRadius * scale))
        {
            g.FillPath(brush, back);
        }

        // Punch a transparent gap where the front screen overlaps the back one. Antialiasing is
        // off for this pass so SourceCopy writes clean zeroes instead of half-transparent black;
        // the supersampled downscale puts the smooth edge back.
        var previousMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        g.CompositingMode = CompositingMode.SourceCopy;
        using (var gap = RoundedRect(Scale(FrontScreen, scale), FrontRadius * scale))
        using (var pen = new Pen(Color.Transparent, GapWidth * scale))
        {
            g.DrawPath(pen, gap);
        }
        g.CompositingMode = CompositingMode.SourceOver;
        g.SmoothingMode = previousMode;

        using (var front = RoundedRect(Scale(FrontScreen, scale), FrontRadius * scale))
        {
            g.FillPath(brush, front);
        }
    }

    private static RectangleF Scale(RectangleF r, float scale)
        => new(r.X * scale, r.Y * scale, r.Width * scale, r.Height * scale);

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
        if (d <= 0.01f)
        {
            path.AddRectangle(r);
            return path;
        }

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Renders at several times the target size and downscales. Small icons get a
    /// bigger factor, so a 16 px icon is still drawn from a 256 px master.</summary>
    public static Bitmap Supersample(int size, Action<Graphics, float> draw)
    {
        var factor = Math.Clamp((int)Math.Ceiling(256.0 / size), 4, 16);
        var big = size * factor;

        using var master = new Bitmap(big, big, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(master))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            draw(g, big / 100f);
        }

        var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(result))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(master, new Rectangle(0, 0, size, size));
        }
        return result;
    }
}
