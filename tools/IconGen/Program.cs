using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Optimus-Vibe app icon: a glowing save crystal floating over a pedestal glow on a navy-to-teal rounded tile.
// All drawing happens in a 256x256 design space and is scaled to the target size.
static class Program
{
    static readonly int[] RenderSizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
    static readonly int[] IcoSizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

    [STAThread]
    static int Main(string[] args)
    {
        string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();
        string? preview = args.Length > 1 ? args[1] : null;
        string assets = Path.Combine(root, "src", "OptimusVibe.App", "Assets");
        Directory.CreateDirectory(assets);

        var bitmaps = RenderSizes.ToDictionary(s => s, Render);
        var pngs = bitmaps.ToDictionary(kv => kv.Key, kv => Encode(kv.Value));

        File.WriteAllBytes(Path.Combine(assets, "app-256.png"), pngs[256]);
        File.WriteAllBytes(Path.Combine(assets, "app-64.png"), pngs[64]);
        string icoPath = Path.Combine(assets, "app.ico");
        WriteIco(icoPath, IcoSizes.Select(s => (s, pngs[s])).ToList());
        Console.WriteLine($"wrote {icoPath}");

        var dec = new IconBitmapDecoder(new Uri(icoPath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Console.WriteLine("ICO frames: " + string.Join(", ", dec.Frames.Select(f => $"{f.PixelWidth}x{f.PixelHeight}")));

        if (preview != null) WritePreview(preview, bitmaps);
        return 0;
    }

    // ---------------------------------------------------------------- drawing
    static BitmapSource Render(int size)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
            Draw(dc, size);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    static Point P(double x, double y) => new(x, y);
    static Brush Frozen(Brush b) { b.Freeze(); return b; }
    static Brush Solid(string hex) => Frozen(new SolidColorBrush(C(hex)));

    static Geometry Poly(params Point[] pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], true, true);
            c.PolyLineTo(pts.Skip(1).ToList(), true, false);
        }
        g.Freeze();
        return g;
    }

    static Brush Radial(Point center, double rx, double ry, params (string hex, double off)[] stops)
    {
        var b = new RadialGradientBrush { Center = center, GradientOrigin = center, RadiusX = rx, RadiusY = ry, MappingMode = BrushMappingMode.Absolute };
        foreach (var (hex, off) in stops) b.GradientStops.Add(new GradientStop(C(hex), off));
        return Frozen(b);
    }

    static Brush Linear(Point a, Point b2, params (string hex, double off)[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = a, EndPoint = b2, MappingMode = BrushMappingMode.Absolute };
        foreach (var (hex, off) in stops) b.GradientStops.Add(new GradientStop(C(hex), off));
        return Frozen(b);
    }

    static Pen PenOf(string hex, double w) { var p = new Pen(Solid(hex), w) { LineJoin = PenLineJoin.Round }; p.Freeze(); return p; }

    static void Draw(DrawingContext dc, int size)
    {
        bool tiny = size <= 24, medium = size > 24 && size <= 40;

        // tile: deep navy (page top) to teal, matching Palette.Default
        var tile = new RectangleGeometry(new Rect(0, 0, 256, 256), 56, 56);
        tile.Freeze();
        dc.DrawGeometry(Linear(P(0, 0), P(256, 256), ("#0B1E30", 0), ("#12344A", 0.55), ("#0F5C6E", 1)), null, tile);
        dc.PushClip(tile);
        // soft warm glow toward the lower-left, as in the app's page background
        dc.DrawRectangle(Radial(P(10, 190), 170, 130, ("#668A6420", 0), ("#008A6420", 1)), null, new Rect(0, 0, 256, 256));
        // crystal halo
        double cy = tiny ? 120 : 114;
        dc.DrawEllipse(Radial(P(128, cy), tiny ? 120 : 112, tiny ? 120 : 112,
            tiny ? ("#9936C8E6", 0) : ("#B336C8E6", 0), ("#5529B6F6", 0.45), ("#0029B6F6", 1)), null, P(128, cy), 130, 130);
        // pedestal glow
        if (!tiny)
        {
            dc.DrawEllipse(Radial(P(128, 226), 66, 12, ("#CC8FE8F5", 0), ("#6629B6F6", 0.5), ("#0029B6F6", 1)), null, P(128, 226), 70, 13);
            if (!medium) dc.DrawEllipse(null, PenOf("#4D8FE8F5", 1.5), P(128, 226), 40, 6.5);
        }
        dc.Pop();

        if (tiny) DrawCrystalSimple(dc, size);
        else DrawCrystalFaceted(dc, medium);

        // sparkle only on large sizes
        if (size >= 64) Sparkle(dc, 188, 54, size >= 128 ? 15 : 12);

        // rim light on the tile edge helps on light taskbars
        var rim = new Pen(Frozen(new SolidColorBrush(Color.FromArgb(tiny ? (byte)0x50 : (byte)0x38, 255, 255, 255))), tiny ? 6 : 2.5);
        rim.Freeze();
        dc.DrawGeometry(null, rim, new RectangleGeometry(new Rect(1.5, 1.5, 253, 253), 54.5, 54.5));
    }

    static void DrawCrystalFaceted(DrawingContext dc, bool medium)
    {
        // octahedron seen slightly from above: apex T, bottom B, equator L-E-M-R (M = front vertex, E splits the left side)
        Point T = P(128, 26), B = P(130, 206), L = P(78, 112), E = P(106, 124), M = P(134, 128), R = P(180, 108);
        if (medium) { T = P(128, 24); B = P(130, 210); L = P(72, 112); R = P(186, 110); }

        void Face(Brush b, params Point[] p) => dc.DrawGeometry(b, null, Poly(p));

        if (medium)
        {
            Face(Solid("#7FE6F5"), T, L, M);              // upper-left lit
            Face(Solid("#3FB8E0"), T, M, R);              // upper-right
            Face(Solid("#1F86B8"), L, B, M);              // lower-left
            Face(Solid("#125A8C"), M, B, R);              // lower-right shadow
        }
        else
        {
            Face(Linear(T, L, ("#E8FDFF", 0), ("#6FD8EE", 1)), T, L, E);       // upper-left outer, brightest side
            Face(Linear(T, M, ("#FFFFFF", 0), ("#8FEAF7", 1)), T, E, M);       // upper-left inner (specular)
            Face(Linear(T, R, ("#7FE0F2", 0), ("#35AEDD", 1)), T, M, R);       // upper-right
            Face(Linear(L, B, ("#3FB6DE", 0), ("#1B7CB0", 1)), L, E, B);       // lower-left outer
            Face(Linear(E, B, ("#2C9ACB", 0), ("#1568A0", 1)), E, M, B);       // lower-left inner
            Face(Linear(R, B, ("#1C7FB4", 0), ("#0F4A7C", 1)), M, R, B);       // lower-right shadow
        }

        // glowing core: white-to-cyan radial clipped to the crystal outline
        var outline = Poly(T, R, B, L);
        dc.PushClip(outline);
        dc.DrawEllipse(Radial(P(124, 104), 56, 70, ("#F2FFFFFF", 0), ("#8CC6F7FF", 0.35), ("#0029D6F6", 1)), null, P(124, 104), 60, 74);
        dc.Pop();

        // facet edges
        dc.DrawGeometry(null, PenOf(medium ? "#B3FFFFFF" : "#99FFFFFF", medium ? 3 : 1.6), outline);
        if (!medium)
        {
            var inner = PenOf("#66FFFFFF", 1.2);
            foreach (var (a, b) in new[] { (T, E), (T, M), (E, M), (L, E), (M, R), (E, B), (M, B) })
                dc.DrawLine(inner, a, b);
        }
        else
        {
            var inner = PenOf("#66FFFFFF", 2);
            dc.DrawLine(inner, T, M); dc.DrawLine(inner, M, B); dc.DrawLine(inner, L, M); dc.DrawLine(inner, M, R);
        }
    }

    static void DrawCrystalSimple(DrawingContext dc, int size)
    {
        // 16-24 px: fat silhouette with a white keyline, four hard-contrast facets, no hairlines
        Point T = P(128, 14), B = P(130, 236), L = P(48, 120), R = P(210, 112);
        dc.DrawGeometry(null, new Pen(Solid("#E6FFFFFF"), size <= 20 ? 7 : 6) { LineJoin = PenLineJoin.Round }, Poly(T, R, B, L));
        dc.DrawGeometry(Solid("#C8FBFF"), null, Poly(P(128, 32), P(60, 118), P(133, 126)));
        dc.DrawGeometry(Solid("#47C6EC"), null, Poly(P(128, 32), P(133, 126), P(198, 112)));
        dc.DrawGeometry(Solid("#1F86C0"), null, Poly(P(60, 123), P(130, 224), P(131, 134)));
        dc.DrawGeometry(Solid("#0D4F86"), null, Poly(P(137, 134), P(130, 224), P(198, 118)));
    }

    static void Sparkle(DrawingContext dc, double x, double y, double r)
    {
        double k = r * 0.2;
        var g = Poly(P(x, y - r), P(x + k, y - k), P(x + r, y), P(x + k, y + k), P(x, y + r), P(x - k, y + k), P(x - r, y), P(x - k, y - k));
        dc.DrawEllipse(Radial(P(x, y), r * 1.6, r * 1.6, ("#80FFFFFF", 0), ("#00FFFFFF", 1)), null, P(x, y), r * 1.6, r * 1.6);
        dc.DrawGeometry(Solid("#FFFFFF"), null, g);
    }

    // ---------------------------------------------------------------- output
    static byte[] Encode(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static void WriteIco(string path, List<(int size, byte[] png)> frames)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)frames.Count);   // ICONDIR
        int offset = 6 + 16 * frames.Count;
        foreach (var (size, png) in frames)                                       // ICONDIRENTRY
        {
            w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write((uint)png.Length); w.Write((uint)offset);
            offset += png.Length;
        }
        foreach (var (_, png) in frames) w.Write(png);
    }

    // nearest-neighbour pixel replication so the preview shows real pixels
    static BitmapSource Magnify(BitmapSource src, int k)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new byte[w * h * 4];
        src.CopyPixels(px, w * 4, 0);
        var big = new byte[w * k * h * k * 4];
        for (int y = 0; y < h * k; y++)
            for (int x = 0; x < w * k; x++)
                Array.Copy(px, ((y / k) * w + x / k) * 4, big, (y * w * k + x) * 4, 4);
        var bs = BitmapSource.Create(w * k, h * k, 96, 96, PixelFormats.Pbgra32, null, big, w * k * 4);
        bs.Freeze();
        return bs;
    }

    static void WritePreview(string path, Dictionary<int, BitmapSource> bmps)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        const int W = 860, StripH = 640;
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.NearestNeighbor);
        using (var dc = dv.RenderOpen())
        {
            string[] bgs = { "#202020", "#F3F3F3" };
            for (int s = 0; s < 2; s++)
            {
                double y0 = s * StripH;
                bool dark = s == 0;
                var fg = dark ? Brushes.White : Brushes.Black;
                dc.DrawRectangle(Solid(bgs[s]), null, new Rect(0, y0, W, StripH));
                void Label(string t, double x, double y) =>
                    dc.DrawText(new FormattedText(t, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), 13, fg, 1.0), P(x, y));
                Label(dark ? "dark taskbar #202020 - 1x" : "light taskbar #F3F3F3 - 1x", 16, y0 + 8);
                double x = 16;
                foreach (var sz in new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 })
                {
                    dc.DrawImage(bmps[sz], new Rect(x, y0 + 36, sz, sz));
                    Label(sz.ToString(), x, y0 + 36 + sz + 4);
                    x += sz + 16;
                }
                Label("magnified 8x (nearest neighbour): 16, 24, 32", 16, y0 + 340);
                x = 16;
                foreach (var sz in new[] { 16, 24, 32 })
                {
                    dc.DrawImage(Magnify(bmps[sz], 8), new Rect(x, y0 + 366, sz * 8, sz * 8));
                    x += sz * 8 + 16;
                }
            }
        }
        var rtb = new RenderTargetBitmap(W, StripH * 2, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        File.WriteAllBytes(path, Encode(rtb));
        Console.WriteLine($"preview {path}");
    }
}
