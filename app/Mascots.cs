using System.Drawing.Drawing2D;

namespace SheepCode;

internal sealed class MascotBanner : Control
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(nint handle);
    internal static void SaveBrandIcon(string path)
    {
        using var bitmap = new Bitmap(64, 64); using var g = Graphics.FromImage(bitmap); g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
        using var background = new SolidBrush(Color.FromArgb(35, 29, 48)); g.FillEllipse(background, 1, 1, 62, 62);
        g.TranslateTransform(3, 16); g.ScaleTransform(0.44f, 0.44f); DrawSheep(g, 0, 0); DrawKuky(g, 68, 0);
        var handle = bitmap.GetHicon(); try { using var icon = Icon.FromHandle(handle); using var output = File.Create(path); icon.Save(output); } finally { DestroyIcon(handle); }
    }
    internal MascotBanner() { DoubleBuffered = true; BackColor = Color.FromArgb(25, 21, 36); Dock = DockStyle.Fill; AccessibleName = "Sheep y Kuky, el gatito sakura"; }
    internal static GraphicsPath Rounded(Rectangle rectangle, int radius)
    {
        var path = new GraphicsPath(); var size = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        if (size <= 0) { path.AddRectangle(rectangle); return path; }
        path.AddArc(rectangle.X, rectangle.Y, size, size, 180, 90); path.AddArc(rectangle.Right - size, rectangle.Y, size, size, 270, 90);
        path.AddArc(rectangle.Right - size, rectangle.Bottom - size, size, size, 0, 90); path.AddArc(rectangle.X, rectangle.Bottom - size, size, size, 90, 90); path.CloseFigure(); return path;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var saved = g.Save(); g.TranslateTransform(2, Math.Max(0, (Height - 86) / 2f)); g.ScaleTransform(Math.Min(Width / 154f, 1.3f), Math.Min(Height / 86f, 1.3f));
        DrawSheep(g, 6, 12); DrawKuky(g, 76, 16);
        using var flower = new SolidBrush(Color.FromArgb(239, 164, 199));
        for (var i = 0; i < 5; i++) { var angle = i * Math.PI * 2 / 5; g.FillEllipse(flower, 68 + (float)Math.Cos(angle) * 5, 5 + (float)Math.Sin(angle) * 5, 5, 5); }
        using var star = new Pen(Color.FromArgb(194, 179, 244), 1.8f);
        foreach (var p in new[] { new Point(4, 6), new Point(137, 15), new Point(71, 72) }) { g.DrawLine(star, p.X - 3, p.Y, p.X + 3, p.Y); g.DrawLine(star, p.X, p.Y - 4, p.X, p.Y + 4); }
        g.Restore(saved);
    }
    private static void DrawSheep(Graphics g, int x, int y)
    {
        using var wool = new SolidBrush(Color.FromArgb(255, 244, 226)); using var face = new SolidBrush(Color.FromArgb(205, 181, 206));
        using var blush = new SolidBrush(Color.FromArgb(248, 168, 191)); using var ink = new Pen(Color.FromArgb(61, 42, 66), 2);
        g.FillEllipse(face, x, y + 24, 20, 12); g.FillEllipse(face, x + 48, y + 24, 20, 12);
        foreach (var p in new[] { new Point(16, 12), new Point(32, 4), new Point(47, 13), new Point(9, 29), new Point(27, 24), new Point(46, 29), new Point(18, 42), new Point(37, 43) }) g.FillEllipse(wool, x + p.X - 8, y + p.Y - 6, 28, 25);
        g.FillEllipse(face, x + 19, y + 22, 31, 34); g.FillEllipse(wool, x + 18, y + 17, 14, 12); g.FillEllipse(wool, x + 30, y + 13, 16, 15);
        g.DrawArc(ink, x + 25, y + 36, 5, 5, 185, 170); g.DrawArc(ink, x + 40, y + 36, 5, 5, 185, 170);
        g.FillEllipse(blush, x + 21, y + 44, 8, 4); g.FillEllipse(blush, x + 41, y + 44, 8, 4); g.DrawArc(ink, x + 31, y + 44, 8, 6, 0, 180);
    }
    private static void DrawKuky(Graphics g, int x, int y)
    {
        using var fur = new SolidBrush(Color.FromArgb(249, 193, 214)); using var inner = new SolidBrush(Color.FromArgb(215, 135, 168)); using var ink = new Pen(Color.FromArgb(78, 49, 74), 1.8f);
        g.FillPolygon(fur, new Point[] { new Point(x + 5, y + 29), new Point(x + 4, y + 2), new Point(x + 26, y + 17) });
        g.FillPolygon(fur, new Point[] { new Point(x + 38, y + 16), new Point(x + 57, y + 1), new Point(x + 59, y + 30) });
        g.FillPolygon(inner, new Point[] { new Point(x + 10, y + 19), new Point(x + 9, y + 8), new Point(x + 20, y + 19) });
        g.FillPolygon(inner, new Point[] { new Point(x + 42, y + 19), new Point(x + 52, y + 8), new Point(x + 53, y + 21) });
        g.FillEllipse(fur, x + 1, y + 15, 62, 46);
        g.DrawArc(ink, x + 15, y + 34, 7, 5, 185, 170); g.DrawArc(ink, x + 42, y + 34, 7, 5, 185, 170);
        g.FillEllipse(inner, x + 9, y + 43, 11, 5); g.FillEllipse(inner, x + 44, y + 43, 11, 5);
        g.FillPolygon(inner, new Point[] { new Point(x + 29, y + 41), new Point(x + 36, y + 41), new Point(x + 32, y + 46) });
        g.DrawArc(ink, x + 24, y + 45, 8, 6, 0, 180); g.DrawArc(ink, x + 32, y + 45, 8, 6, 0, 180);
        g.DrawLine(ink, x + 2, y + 41, x + 12, y + 43); g.DrawLine(ink, x + 54, y + 43, x + 64, y + 40);
        using var petal = new SolidBrush(Color.FromArgb(244, 157, 195)); for (var i = 0; i < 5; i++) { var a = i * Math.PI * 2 / 5; g.FillEllipse(petal, x + 52 + (float)Math.Cos(a) * 5, y + 9 + (float)Math.Sin(a) * 5, 7, 7); }
        using var center = new SolidBrush(Color.FromArgb(255, 225, 160)); g.FillEllipse(center, x + 54, y + 11, 4, 4);
    }
}
