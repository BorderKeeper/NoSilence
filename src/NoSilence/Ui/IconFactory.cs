using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace NoSilence.Ui;

/// <summary>
/// Draws the NoSilence music-note glyph and packs it into Windows icons.
/// <para>
/// Deliberately free of any NoSilence types and of anything outside
/// <c>System.Drawing</c>: <c>tools/make-icons.ps1</c> compiles this exact file
/// standalone to generate <c>assets/NoSilence.ico</c> at design time, and the tray
/// uses it at runtime to render DPI-correct icons. Keep it self-contained.
/// </para>
/// </summary>
internal static class IconFactory
{
    /// <summary>Sizes packed into the shipped application icon.</summary>
    public static readonly int[] AppIconSizes = [16, 20, 24, 32, 48, 64, 128, 256];

    /// <summary>
    /// Renders the note glyph at <paramref name="size"/> square.
    /// </summary>
    /// <param name="background">Rounded-square plate behind the note. Pass
    /// <see cref="Color.Transparent"/> for tray icons, where a plate reads as clutter.</param>
    /// <param name="note">Note colour.</param>
    /// <param name="badge">Optional status dot in the lower-right corner.</param>
    public static Bitmap RenderNote(int size, Color background, Color note, Color? badge = null)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        bmp.SetResolution(96f, 96f);

        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float s = size;

        if (background.A > 0)
        {
            float radius = s * 0.22f;
            using var plate = RoundedRect(0.5f, 0.5f, s - 1f, s - 1f, radius);
            using var brush = new SolidBrush(background);
            g.FillPath(brush, plate);
        }

        // Note geometry, expressed in a 0..1 unit box so it scales exactly.
        // Inset a little when there is a plate so the glyph does not touch the edge.
        float pad = background.A > 0 ? 0.15f : 0.06f;
        float span = 1f - (pad * 2f);
        float X(float u) => (pad + (u * span)) * s;
        float Y(float v) => (pad + (v * span)) * s;

        using (var noteBrush = new SolidBrush(note))
        {
            // Stem.
            float stemLeft = X(0.55f);
            float stemRight = X(0.68f);
            float stemTop = Y(0.04f);
            float stemBottom = Y(0.76f);
            g.FillRectangle(noteBrush, stemLeft, stemTop, stemRight - stemLeft, stemBottom - stemTop);

            // Flag.
            using var flag = new GraphicsPath();
            flag.AddBezier(
                new PointF(stemRight, stemTop),
                new PointF(X(0.95f), Y(0.10f)),
                new PointF(X(0.98f), Y(0.26f)),
                new PointF(X(0.86f), Y(0.40f)));
            flag.AddBezier(
                new PointF(X(0.86f), Y(0.40f)),
                new PointF(X(0.92f), Y(0.24f)),
                new PointF(X(0.84f), Y(0.16f)),
                new PointF(stemRight, Y(0.22f)));
            flag.CloseFigure();
            g.FillPath(noteBrush, flag);

            // Head.
            float headW = span * s * 0.46f;
            float headH = span * s * 0.34f;
            var head = new RectangleF(stemLeft + ((stemRight - stemLeft) / 2f) - headW, stemBottom - (headH * 0.75f), headW, headH);
            var saved = g.Save();
            g.TranslateTransform(head.X + (head.Width / 2f), head.Y + (head.Height / 2f));
            g.RotateTransform(-20f);
            g.FillEllipse(noteBrush, -head.Width / 2f, -head.Height / 2f, head.Width, head.Height);
            g.Restore(saved);
        }

        if (badge is { } dot)
        {
            float d = MathF.Max(4f, s * 0.34f);
            var rect = new RectangleF(s - d - (s * 0.02f), s - d - (s * 0.02f), d, d);
            using (var halo = new SolidBrush(Color.Transparent))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.FillEllipse(halo, RectangleF.Inflate(rect, MathF.Max(1f, s * 0.05f), MathF.Max(1f, s * 0.05f)));
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            }

            using var dotBrush = new SolidBrush(dot);
            g.FillEllipse(dotBrush, rect);
        }

        return bmp;
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        r = MathF.Min(r, MathF.Min(w, h) / 2f);
        float d = r * 2f;
        var path = new GraphicsPath();
        if (r <= 0.01f)
        {
            path.AddRectangle(new RectangleF(x, y, w, h));
            return path;
        }

        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Writes a multi-resolution <c>.ico</c>. Entries up to 64px are stored as 32-bit
    /// DIBs (universally understood); larger entries are stored as PNG, which every
    /// Windows since Vista reads and which keeps the file small.
    /// </summary>
    public static void WriteIco(string path, Color background, Color note, int[]? sizes = null)
    {
        sizes ??= AppIconSizes;

        var payloads = new List<(int Size, byte[] Data, bool IsPng)>(sizes.Length);
        foreach (int size in sizes.Distinct().OrderBy(v => v))
        {
            using Bitmap bmp = RenderNote(size, background, note);
            bool png = size > 64;
            payloads.Add((size, png ? EncodePng(bmp) : EncodeDib(bmp), png));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var w = new BinaryWriter(fs);

        w.Write((ushort)0);                    // reserved
        w.Write((ushort)1);                    // type: icon
        w.Write((ushort)payloads.Count);

        int offset = 6 + (16 * payloads.Count);
        foreach (var (size, data, _) in payloads)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);                  // palette entries
            w.Write((byte)0);                  // reserved
            w.Write((ushort)1);                // colour planes
            w.Write((ushort)32);               // bits per pixel
            w.Write(data.Length);
            w.Write(offset);
            offset += data.Length;
        }

        foreach (var (_, data, _) in payloads)
        {
            w.Write(data);
        }
    }

    /// <summary>
    /// Writes a contact sheet of every tray state at every size we render, on both a dark
    /// and a light strip. Design-time only: it is far quicker to look at this than to
    /// squint at a 16px icon in the corner of the screen.
    /// </summary>
    public static void WritePreviewSheet(string path)
    {
        int[] sizes = [16, 20, 24, 32, 48, 64];
        (string Label, Color Note, Color? Badge)[] states =
        [
            ("playing", Color.FromArgb(0x4C, 0x8D, 0xFF), null),
            ("ducked", Color.FromArgb(0x8A, 0x90, 0x9A), null),
            ("waiting", Color.FromArgb(0x8A, 0x90, 0x9A), Color.FromArgb(0xE0, 0xA1, 0x06)),
            ("disabled", Color.FromArgb(0x70, 0x8A, 0x90, 0x9A), null),
            ("error", Color.FromArgb(0x8A, 0x90, 0x9A), Color.FromArgb(0xE5, 0x47, 0x4D)),
            ("app icon", Color.FromArgb(0x7A, 0xB0, 0xFF), null),
        ];

        const int Gutter = 12;
        const int LabelWidth = 90;
        const int Scale = 3;   // draw each icon at 1x and at 3x, nearest-neighbour
        int rowHeight = (64 * Scale) + Gutter;
        int width = LabelWidth + sizes.Sum(s => (s * Scale) + Gutter) + Gutter;
        int height = (states.Length * rowHeight) + Gutter;

        using var sheet = new Bitmap(width, height * 2, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(sheet);
        using var font = new Font("Segoe UI", 9f);

        foreach ((Color backdrop, int yOffset, Color ink) in new[]
        {
            (Color.FromArgb(0x1E, 0x1E, 0x22), 0, Color.White),
            (Color.FromArgb(0xF3, 0xF3, 0xF5), height, Color.Black),
        })
        {
            using var backdropBrush = new SolidBrush(backdrop);
            using var inkBrush = new SolidBrush(ink);
            g.FillRectangle(backdropBrush, 0, yOffset, width, height);

            int y = yOffset + Gutter;
            foreach ((string label, Color note, Color? badge) in states)
            {
                g.DrawString(label, font, inkBrush, 6, y + 8);

                int x = LabelWidth;
                foreach (int size in sizes)
                {
                    // The app icon row carries its plate; tray rows are transparent.
                    Color plate = label == "app icon" ? Color.FromArgb(0x1E, 0x29, 0x3B) : Color.Transparent;
                    using Bitmap bmp = RenderNote(size, plate, note, badge);

                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(bmp, x, y, size * Scale, size * Scale);
                    g.DrawImage(bmp, x, y + (64 * Scale) - size - 2, size, size);

                    x += (size * Scale) + Gutter;
                }

                y += rowHeight;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        sheet.Save(path, ImageFormat.Png);
    }

    private static byte[] EncodePng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>
    /// Packs a bitmap as a BITMAPINFOHEADER + bottom-up BGRA XOR mask + 1bpp AND mask,
    /// which is what an ICO directory entry expects. Note <c>biHeight</c> is doubled:
    /// the header describes both masks stacked.
    /// </summary>
    private static byte[] EncodeDib(Bitmap bmp)
    {
        int width = bmp.Width;
        int height = bmp.Height;
        int andStride = ((width + 31) / 32) * 4;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write(40);                           // biSize
        w.Write(width);                        // biWidth
        w.Write(height * 2);                   // biHeight (XOR + AND)
        w.Write((ushort)1);                    // biPlanes
        w.Write((ushort)32);                   // biBitCount
        w.Write(0);                            // biCompression = BI_RGB
        w.Write((width * 4 * height) + (andStride * height));
        w.Write(0);                            // biXPelsPerMeter
        w.Write(0);                            // biYPelsPerMeter
        w.Write(0);                            // biClrUsed
        w.Write(0);                            // biClrImportant

        BitmapData bits = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[width * 4];
            for (int y = height - 1; y >= 0; y--)   // DIBs are stored bottom-up
            {
                nint src = bits.Scan0 + (y * bits.Stride);
                System.Runtime.InteropServices.Marshal.Copy(src, row, 0, row.Length);
                w.Write(row);
            }

            // AND mask: fully transparent where alpha is 0, so legacy renderers cut the
            // right hole. Modern Windows uses the alpha channel, but the mask must exist.
            var andRow = new byte[andStride];
            for (int y = height - 1; y >= 0; y--)
            {
                Array.Clear(andRow);
                nint src = bits.Scan0 + (y * bits.Stride);
                System.Runtime.InteropServices.Marshal.Copy(src, row, 0, row.Length);
                for (int x = 0; x < width; x++)
                {
                    if (row[(x * 4) + 3] == 0)
                    {
                        andRow[x / 8] |= (byte)(0x80 >> (x % 8));
                    }
                }

                w.Write(andRow);
            }
        }
        finally
        {
            bmp.UnlockBits(bits);
        }

        w.Flush();
        return ms.ToArray();
    }
}
