using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace DisplaySwitch;

/// <summary>Builds the tray icon at runtime, so the app carries no binary assets.
/// It is the logo tile with the active layout's initial on it -- the tray has to answer
/// "which layout am I in?" at a glance, which the bare mark cannot do.</summary>
internal static class IconFactory
{
    private static readonly int[] TraySizes = { 16, 20, 24, 32, 48 };

    /// <summary>Gradient tile carrying <paramref name="label"/>, or the plain mark on a muted
    /// tile when the current layout matches no preset.</summary>
    public static Icon BuildTray(string? label)
    {
        using var stream = new MemoryStream();
        IcoWriter.Write(stream, size => RenderTrayBitmap(size, label), TraySizes);
        stream.Position = 0;
        return new Icon(stream);
    }

    private static Bitmap RenderTrayBitmap(int size, string? label)
    {
        var text = Normalise(label);

        return Logo.Supersample(size, (g, scale) =>
        {
            var edge = 100f * scale;
            using (var path = Logo.RoundedRect(new RectangleF(0, 0, edge, edge), 22f * scale))
            {
                if (text is null)
                {
                    using var flat = new SolidBrush(Color.FromArgb(0x3A, 0x3A, 0x42));
                    g.FillPath(flat, path);
                }
                else
                {
                    using var gradient = new LinearGradientBrush(
                        new RectangleF(0, 0, edge, edge), Logo.GradientStart, Logo.GradientEnd, 55f);
                    g.FillPath(gradient, path);
                }
            }

            if (text is null)
            {
                DrawMuteMark(g, scale);
                return;
            }

            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            // One character can be set large enough to read at 16 px; two must be pulled back.
            var emSize = (text.Length == 1 ? 66f : 46f) * scale;
            using var font = new Font("Segoe UI", emSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.White);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            // Segoe UI sits slightly high in its line box; nudge down so it optically centres.
            var box = new RectangleF(0, 3f * scale, edge, edge);
            g.DrawString(text, font, brush, box, format);
        });
    }

    /// <summary>The mark, drawn small and muted, for "this layout isn't one of yours".</summary>
    private static void DrawMuteMark(Graphics g, float scale)
    {
        using var mark = Logo.RenderMark((int)(100f * scale), Color.FromArgb(0xC8, 0xC8, 0xD0));
        g.DrawImage(mark, 0, 0);
    }

    private static string? Normalise(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var text = new string(label.Where(char.IsLetterOrDigit).Take(2).ToArray()).ToUpperInvariant();
        return text.Length == 0 ? null : text;
    }

    /// <summary>The shortest prefix that still tells this preset apart from the others, so the
    /// tray can use a single big letter when the names allow it.</summary>
    public static string DistinctLabel(Preset preset, IEnumerable<Preset> all)
    {
        var name = new string(preset.Name.Where(char.IsLetterOrDigit).ToArray());
        if (name.Length <= 1) return name;

        var firstChar = char.ToUpperInvariant(name[0]);
        var collides = all
            .Where(p => !ReferenceEquals(p, preset))
            .Select(p => new string(p.Name.Where(char.IsLetterOrDigit).ToArray()))
            .Any(other => other.Length > 0 && char.ToUpperInvariant(other[0]) == firstChar);

        return collides ? name[..2] : name[..1];
    }
}
