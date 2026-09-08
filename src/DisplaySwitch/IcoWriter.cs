using System.Drawing;
using System.Drawing.Imaging;

namespace DisplaySwitch;

/// <summary>Writes a multi-resolution .ico. Small sizes go in as 32-bit DIBs (what Explorer and
/// the shell prefer); the big ones go in as PNG to keep the file small.</summary>
internal static class IcoWriter
{
    public static readonly int[] StandardSizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    public static void Write(string path, Func<int, Bitmap> render, IEnumerable<int>? sizes = null)
    {
        using var stream = File.Create(path);
        Write(stream, render, sizes);
    }

    public static void Write(Stream stream, Func<int, Bitmap> render, IEnumerable<int>? sizes = null)
    {
        var wanted = (sizes ?? StandardSizes).Distinct().OrderBy(s => s).ToArray();
        var images = new List<(int Size, byte[] Data)>();

        foreach (var size in wanted)
        {
            using var bmp = render(size);
            images.Add((size, size >= 64 ? EncodePng(bmp) : EncodeDib(bmp)));
        }

        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write((ushort)0);                 // reserved
        writer.Write((ushort)1);                 // type: icon
        writer.Write((ushort)images.Count);

        var offset = 6 + images.Count * 16;
        foreach (var (size, data) in images)
        {
            writer.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);               // palette entries
            writer.Write((byte)0);               // reserved
            writer.Write((ushort)1);             // colour planes
            writer.Write((ushort)32);            // bits per pixel
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }

        foreach (var (_, data) in images) writer.Write(data);
    }

    private static byte[] EncodePng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>BITMAPINFOHEADER + bottom-up BGRA pixels + an (unused, all-zero) AND mask.
    /// The height field is doubled because the format counts both planes.</summary>
    private static byte[] EncodeDib(Bitmap bmp)
    {
        var w = bmp.Width;
        var h = bmp.Height;
        var andStride = (w + 31) / 32 * 4;
        var andSize = andStride * h;
        var xorSize = w * h * 4;

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(40);                  // biSize
        writer.Write(w);                   // biWidth
        writer.Write(h * 2);               // biHeight (XOR + AND)
        writer.Write((ushort)1);           // biPlanes
        writer.Write((ushort)32);          // biBitCount
        writer.Write(0);                   // biCompression = BI_RGB
        writer.Write(xorSize + andSize);   // biSizeImage
        writer.Write(0);                   // biXPelsPerMeter
        writer.Write(0);                   // biYPelsPerMeter
        writer.Write(0);                   // biClrUsed
        writer.Write(0);                   // biClrImportant

        var data = bmp.LockBits(
            new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (var y = h - 1; y >= 0; y--)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + y * data.Stride, row, 0, row.Length);
                writer.Write(row);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        writer.Write(new byte[andSize]);
        return ms.ToArray();
    }
}
