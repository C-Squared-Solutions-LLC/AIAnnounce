using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace AIAnnounce;

// Code-native marks stay sharp at every Windows DPI. The application mark is provider-neutral.
static class BrandIcons
{
    public static Color ColorFor(Cli provider) => provider switch
    { Cli.Claude => Color.FromArgb(217, 119, 87), Cli.Codex => Color.FromArgb(233, 235, 242), _ => Color.FromArgb(132, 177, 255) };

    public static void DrawApp(Graphics g, Rectangle r)
    {
        var state = g.Save();
        g.TranslateTransform(r.X, r.Y);
        g.ScaleTransform(r.Width / 32f, r.Height / 32f);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var bg = new SolidBrush(Color.FromArgb(35, 42, 58));
        using var shape = Rounded(new RectangleF(1, 1, 30, 30), 8);
        g.FillPath(bg, shape);
        using var link = new Pen(Color.FromArgb(171, 197, 235), 2.4f) { LineJoin = LineJoin.Round };
        g.DrawPolygon(link, new[] { new PointF(16, 8), new PointF(24, 22), new PointF(8, 22) });
        var points = new[] { new PointF(16, 8), new PointF(24, 22), new PointF(8, 22) };
        var colors = new[] { Color.FromArgb(128, 215, 238), Color.FromArgb(173, 154, 245), Color.FromArgb(203, 222, 239) };
        for (int i = 0; i < points.Length; i++)
        {
            using var brush = new SolidBrush(colors[i]);
            g.FillEllipse(brush, points[i].X - 3.6f, points[i].Y - 3.6f, 7.2f, 7.2f);
        }
        g.Restore(state);
    }

    public static void DrawProvider(Graphics g, Rectangle r, Cli provider)
    {
        var state = g.Save();
        g.TranslateTransform(r.X, r.Y);
        g.ScaleTransform(r.Width / 24f, r.Height / 24f);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (provider == Cli.Claude)
        {
            using var p = new Pen(ColorFor(provider), 2.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            for (int i = 0; i < 12; i++)
            {
                double angle = i * Math.PI / 6;
                float outer = i % 3 == 0 ? 10 : 8.5f;
                g.DrawLine(p, 12 + (float)Math.Cos(angle) * 2, 12 + (float)Math.Sin(angle) * 2,
                    12 + (float)Math.Cos(angle) * outer, 12 + (float)Math.Sin(angle) * outer);
            }
        }
        else if (provider == Cli.Codex)
        {
            // Six interlocked loops, the OpenAI provider mark.
            g.TranslateTransform(12, 12);
            using var p = new Pen(ColorFor(provider), 1.7f) { LineJoin = LineJoin.Round };
            for (int i = 0; i < 6; i++)
            {
                using var loop = new GraphicsPath();
                loop.AddBezier(-1, -4, -7, -10, 4, -13, 6, -6);
                loop.AddLine(6, -6, 6, 1);
                loop.AddLine(6, 1, 0, 4.5f);
                g.DrawPath(p, loop);
                g.RotateTransform(60);
            }
        }
        else
        {
            // Gemini's four-point sparkle; agy is used exclusively for Gemini sessions here.
            using var path = new GraphicsPath();
            path.AddBezier(12, 1, 13.8f, 7.6f, 16.4f, 10.2f, 23, 12);
            path.AddBezier(23, 12, 16.4f, 13.8f, 13.8f, 16.4f, 12, 23);
            path.AddBezier(12, 23, 10.2f, 16.4f, 7.6f, 13.8f, 1, 12);
            path.AddBezier(1, 12, 7.6f, 10.2f, 10.2f, 7.6f, 12, 1);
            path.CloseFigure();
            using var fill = new LinearGradientBrush(new Point(2, 8), new Point(22, 16), Color.FromArgb(105, 165, 255), Color.FromArgb(211, 132, 218));
            g.FillPath(fill, path);
        }
        g.Restore(state);
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Icon AppIcon()
    {
        using var stream = new MemoryStream();
        WriteIcon(stream);
        stream.Position = 0;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    public static void WriteIcon(Stream stream)
    {
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        var images = new List<byte[]>();
        foreach (int size in sizes)
        {
            using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) DrawApp(g, new Rectangle(0, 0, size, size));
            using var png = new MemoryStream();
            bmp.Save(png, ImageFormat.Png);
            images.Add(png.ToArray());
        }
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
            writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(images[i].Length); writer.Write(offset); offset += images[i].Length;
        }
        foreach (var png in images) writer.Write(png);
    }
}
