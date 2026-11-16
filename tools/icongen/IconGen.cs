using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class IconGen
{
    private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

    public static int Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : "sam.ico";
        var frames = new List<byte[]>();
        foreach (int size in Sizes)
        {
            using (Bitmap bmp = Render(size))
            {
                frames.Add(ToDib(bmp));
            }
        }
        using (var fs = new FileStream(output, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((ushort)0);
            w.Write((ushort)1);
            w.Write((ushort)Sizes.Length);
            int offset = 6 + 16 * Sizes.Length;
            for (int i = 0; i < Sizes.Length; i++)
            {
                int s = Sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((ushort)1);
                w.Write((ushort)32);
                w.Write((uint)frames[i].Length);
                w.Write((uint)offset);
                offset += frames[i].Length;
            }
            foreach (byte[] frame in frames) w.Write(frame);
        }
        Console.WriteLine("icon written: " + output);
        return 0;
    }

    private static byte[] ToDib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write((uint)40);
            bw.Write((int)w);
            bw.Write((int)(h * 2));
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write((uint)0);
            bw.Write((uint)(w * h * 4));
            bw.Write((int)0);
            bw.Write((int)0);
            bw.Write((uint)0);
            bw.Write((uint)0);

            for (int y = h - 1; y >= 0; y--)
            {
                for (int x = 0; x < w; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    bw.Write(c.B);
                    bw.Write(c.G);
                    bw.Write(c.R);
                    bw.Write(c.A);
                }
            }

            int rowBytes = ((w + 31) / 32) * 4;
            for (int y = h - 1; y >= 0; y--)
            {
                var row = new byte[rowBytes];
                for (int x = 0; x < w; x++)
                {
                    if (bmp.GetPixel(x, y).A == 0)
                    {
                        row[x >> 3] |= (byte)(0x80 >> (x & 7));
                    }
                }
                bw.Write(row);
            }
            bw.Flush();
            return ms.ToArray();
        }
    }

    private static Bitmap Render(int size)
    {
        int scale = 4;
        int big = size * scale;
        using (var canvas = new Bitmap(big, big, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                float pad = big * 0.045f;
                var box = new RectangleF(pad, pad, big - pad * 2, big - pad * 2);
                float radius = big * 0.235f;
                using (GraphicsPath round = RoundedRect(box, radius))
                using (var brush = new LinearGradientBrush(box, Color.FromArgb(124, 92, 255), Color.FromArgb(74, 168, 255), 45f))
                {
                    g.FillPath(brush, round);
                }

                using (var glow = new LinearGradientBrush(box, Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                {
                    using (GraphicsPath round = RoundedRect(box, radius))
                    {
                        g.FillPath(glow, round);
                    }
                }

                DrawTrophy(g, big);
            }

            var small = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics sg = Graphics.FromImage(small))
            {
                sg.SmoothingMode = SmoothingMode.AntiAlias;
                sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                sg.CompositingQuality = CompositingQuality.HighQuality;
                sg.DrawImage(canvas, new Rectangle(0, 0, size, size));
            }
            return small;
        }
    }

    private static void DrawTrophy(Graphics g, int big)
    {
        float S = big;
        using (var white = new SolidBrush(Color.FromArgb(246, 255, 255, 255)))
        {
            using (var path = new GraphicsPath())
            {
                float left = S * 0.305f, right = S * 0.695f, top = S * 0.255f;
                path.AddLine(left, top, right, top);
                path.AddBezier(right, top, right - S * 0.005f, S * 0.50f, S * 0.60f, S * 0.615f, S * 0.5f, S * 0.615f);
                path.AddBezier(S * 0.5f, S * 0.615f, S * 0.40f, S * 0.615f, left + S * 0.005f, S * 0.50f, left, top);
                path.CloseFigure();
                g.FillPath(white, path);
            }

            DrawTrophyBody(g, white, S);
        }

        DrawTrophyArcs(g, S);
    }

    private static void DrawTrophyBody(Graphics g, Brush white, float S)
    {
        float stemW = S * 0.072f;
        g.FillRectangle(white, S * 0.5f - stemW / 2, S * 0.60f, stemW, S * 0.105f);
        using (var wing = RoundedRect(new RectangleF(S * 0.30f, S * 0.70f, S * 0.40f, S * 0.075f), S * 0.037f))
        {
            g.FillPath(white, wing);
        }
    }

    private static void DrawTrophyArcs(Graphics g, float S)
    {
        using (var pen = new Pen(Color.FromArgb(238, 255, 255, 255), Math.Max(1.4f, S * 0.028f)))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            g.DrawArc(pen, new RectangleF(S * 0.155f, S * 0.285f, S * 0.24f, S * 0.245f), 90, 210);
            g.DrawArc(pen, new RectangleF(S * 0.605f, S * 0.285f, S * 0.24f, S * 0.245f), -120, 210);
        }

        using (var spark = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
        {
            var star = new[]
            {
                new PointF(S * 0.775f, S * 0.185f),
                new PointF(S * 0.805f, S * 0.245f),
                new PointF(S * 0.865f, S * 0.275f),
                new PointF(S * 0.805f, S * 0.305f),
                new PointF(S * 0.775f, S * 0.365f),
                new PointF(S * 0.745f, S * 0.305f),
                new PointF(S * 0.685f, S * 0.275f),
                new PointF(S * 0.745f, S * 0.245f)
            };
            g.FillPolygon(spark, star);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
