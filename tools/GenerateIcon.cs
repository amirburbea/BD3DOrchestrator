#:property TargetFramework=net11.0-windows
#:property UseWPF=true
#:property ImplicitUsings=disable
#:property Nullable=enable
#:property PublishAot=false

// Draws the BD3DOrchestrator icon (red/cyan 3D glasses) as vectors at every icon size and packs the PNGs into one multi-resolution .ico.
// Each size is drawn separately (not downscaled), so small sizes can get simplified, pixel-friendly detail.
//
//   dotnet run tools/GenerateIcon.cs -- BD3DOrchestrator/app.ico [preview.png]

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

string icoPath = args.Length > 0 ? args[0] : "app.ico";
string? previewPath = args.Length > 1 ? args[1] : null;

// WPF rendering needs an STA thread.
Thread thread = new(() => IconGenerator.Run(icoPath, previewPath));
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

static class IconGenerator
{
    private static readonly int[] _sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    private static readonly Color _tileTop = Color.FromRgb(0x24, 0x28, 0x4D);
    private static readonly Color _tileBottom = Color.FromRgb(0x12, 0x14, 0x2B);
    private static readonly Color _red = Color.FromRgb(0xFF, 0x3B, 0x5C);
    private static readonly Color _cyan = Color.FromRgb(0x1F, 0xD6, 0xFF);
    private static readonly Color _frame = Color.FromRgb(0xF7, 0xF8, 0xFC);

    public static void Run(string icoPath, string? previewPath)
    {
        List<(int Size, BitmapSource Bitmap, byte[] Png)> images = [];
        foreach (int size in _sizes)
        {
            BitmapSource bitmap = Render(size);
            images.Add((size, bitmap, EncodePng(bitmap)));
        }
        WriteIco(icoPath, images);
        Console.WriteLine($"Wrote {Path.GetFullPath(icoPath)} ({string.Join(", ", _sizes)} px)");
        if (previewPath is not null)
        {
            WritePreview(previewPath, images);
            Console.WriteLine($"Wrote {Path.GetFullPath(previewPath)}");
        }
    }

    /// <summary>
    /// Red/cyan anaglyph 3D glasses on a navy tile. Coordinates are on a 256-unit canvas; small sizes get a thicker
    /// frame and bigger lenses, and drop the details that would only blur.
    /// </summary>
    private static void Draw(DrawingContext dc, int size)
    {
        bool tiny = size <= 24;
        bool small = size <= 32;

        // Tile. Tiny sizes use the full square so every pixel counts.
        double inset = tiny ? 0 : 10;
        double corner = tiny ? 44 : 56;
        dc.DrawRoundedRectangle(new LinearGradientBrush(_tileTop, _tileBottom, 90), null, new Rect(inset, inset, 256 - 2 * inset, 256 - 2 * inset), corner, corner);

        double stroke = tiny ? 22 : small ? 18 : 12;
        double width = tiny ? 232 : 204;   // both lenses and the bridge, excluding the side tabs
        double gap = tiny ? 30 : 28;       // between the lenses, spanned by the bridge
        double height = tiny ? 108 : 86;
        double lensWidth = (width - gap) / 2;
        double top = 128 - height / 2;
        double leftLensX = 128 - width / 2;
        double rightLensX = 128 + gap / 2;

        Pen frame = new(new SolidColorBrush(_frame), stroke) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        Geometry leftLens = Lens(leftLensX, top, lensWidth, height, mirrored: false);
        Geometry rightLens = Lens(rightLensX, top, lensWidth, height, mirrored: true);

        // Side tabs where the arms would hinge.
        if (!tiny)
        {
            double tab = 8; // short enough that the round cap stays inside the tile
            dc.DrawLine(frame, new Point(leftLensX - tab, top + stroke / 2), new Point(leftLensX + 4, top + stroke / 2));
            dc.DrawLine(frame, new Point(rightLensX + lensWidth - 4, top + stroke / 2), new Point(rightLensX + lensWidth + tab, top + stroke / 2));
        }

        // Bridge.
        StreamGeometry bridge = new();
        using (StreamGeometryContext ctx = bridge.Open())
        {
            double y = top + height * 0.3;
            ctx.BeginFigure(new Point(leftLensX + lensWidth - 2, y), false, false);
            ctx.QuadraticBezierTo(new Point(128, top - height * 0.05), new Point(rightLensX + 2, y), true, false);
        }
        dc.DrawGeometry(null, frame, bridge);

        DrawLens(dc, leftLens, _red, frame, shine: size >= 48);
        DrawLens(dc, rightLens, _cyan, frame, shine: size >= 48);
    }

    private static void DrawLens(DrawingContext dc, Geometry lens, Color color, Pen frame, bool shine)
    {
        dc.DrawGeometry(new SolidColorBrush(color), null, lens);
        if (shine)
        {
            // A soft diagonal band of light across the lens.
            Rect b = lens.Bounds;
            StreamGeometry band = new();
            using (StreamGeometryContext ctx = band.Open())
            {
                ctx.BeginFigure(new Point(b.Left + b.Width * 0.18, b.Top), true, true);
                ctx.LineTo(new Point(b.Left + b.Width * 0.46, b.Top), true, false);
                ctx.LineTo(new Point(b.Left + b.Width * 0.16, b.Bottom), true, false);
                ctx.LineTo(new Point(b.Left - b.Width * 0.12, b.Bottom), true, false);
            }
            dc.PushClip(lens);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)), null, band);
            dc.Pop();
        }
        dc.DrawGeometry(null, frame, lens);
    }

    /// <summary>A lens: flat top, rounder bottom, tapering slightly towards the nose.</summary>
    private static Geometry Lens(double x, double y, double w, double h, bool mirrored)
    {
        double taper = w * 0.08;
        // Clockwise from top-left, for the left lens (nose on the right).
        Point[] points =
        [
            new(x, y),
            new(x + w, y),
            new(x + w - taper, y + h),
            new(x + taper * 0.4, y + h),
        ];
        double[] radii = [h * 0.16, h * 0.16, h * 0.38, h * 0.38];
        if (mirrored)
        {
            // Flip horizontally about the lens centre so the taper faces the nose.
            for (int i = 0; i < points.Length; i++)
            {
                points[i] = new Point(2 * x + w - points[i].X, points[i].Y);
            }
            Array.Reverse(points, 0, 2);
            Array.Reverse(points, 2, 2);
        }
        return RoundedPolygon(points, radii);
    }

    /// <summary>A closed polygon whose corners are rounded with quadratic curves of the given (approximate) radii.</summary>
    private static Geometry RoundedPolygon(Point[] points, double[] radii)
    {
        StreamGeometry geometry = new();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            int n = points.Length;
            for (int i = 0; i < n; i++)
            {
                Point previous = points[(i + n - 1) % n], corner = points[i], next = points[(i + 1) % n];
                Point start = Toward(corner, previous, radii[i]);
                Point end = Toward(corner, next, radii[i]);
                if (i == 0)
                {
                    ctx.BeginFigure(start, true, true);
                }
                else
                {
                    ctx.LineTo(start, true, true);
                }
                ctx.QuadraticBezierTo(corner, end, true, true);
            }
        }
        geometry.Freeze();
        return geometry;

        static Point Toward(Point from, Point to, double distance)
        {
            Vector v = to - from;
            double length = v.Length;
            return from + v / length * Math.Min(distance, length / 2);
        }
    }

    private static BitmapSource Render(int size)
    {
        DrawingVisual visual = new();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
            Draw(dc, size);
            dc.Pop();
        }
        RenderTargetBitmap bitmap = new(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] EncodePng(BitmapSource bitmap)
    {
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using MemoryStream stream = new();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>ICO with PNG-compressed entries (supported since Windows Vista).</summary>
    private static void WriteIco(string path, List<(int Size, BitmapSource Bitmap, byte[] Png)> images)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } dir)
        {
            Directory.CreateDirectory(dir);
        }
        using BinaryWriter writer = new(File.Create(path));
        writer.Write((ushort)0); // reserved
        writer.Write((ushort)1); // type: icon
        writer.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach ((int size, _, byte[] png) in images)
        {
            writer.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);   // palette size
            writer.Write((byte)0);   // reserved
            writer.Write((ushort)1); // colour planes
            writer.Write((ushort)32);
            writer.Write(png.Length);
            writer.Write(offset);
            offset += png.Length;
        }
        foreach ((_, _, byte[] png) in images)
        {
            writer.Write(png);
        }
    }

    /// <summary>Every size at 1:1 on light and dark strips, plus the small sizes magnified 4x to show the pixels.</summary>
    private static void WritePreview(string path, List<(int Size, BitmapSource Bitmap, byte[] Png)> images)
    {
        const int width = 1100;
        const int height = 900;
        DrawingVisual visual = new();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)), null, new Rect(0, 0, width, 300));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(0, 300, width, height - 300));
            double x = 20;
            foreach ((int size, BitmapSource bitmap, _) in images)
            {
                dc.DrawImage(bitmap, new Rect(x, 20, size, size));
                dc.DrawImage(bitmap, new Rect(x, 320, size, size));
                x += size + 16;
            }
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
            x = 20;
            foreach ((int size, BitmapSource bitmap, _) in images)
            {
                if (size > 48)
                {
                    break;
                }
                dc.DrawImage(bitmap, new Rect(x, 610, size * 4, size * 4));
                x += size * 4 + 20;
            }
        }
        RenderTargetBitmap sheet = new(width, height, 96, 96, PixelFormats.Pbgra32);
        sheet.Render(visual);
        File.WriteAllBytes(path, EncodePng(sheet));
    }
}
