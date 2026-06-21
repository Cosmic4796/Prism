using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RobloxMultiManager.UI;

/// <summary>
/// Minimal native theming for Prism's host windows. The main UI is a web app
/// (UI/web/app.html) that owns its own styling; this only handles the host form
/// background, the Windows-11 dark title bar, and the generated prism app icon.
/// </summary>
internal static class Theme
{
    public const string AppName = "Prism";
    public const string AppTitle = "Prism — Roblox Account Manager";

    public static readonly Color Bg = Color.FromArgb(22, 23, 26);
    public static readonly Color Surface = Color.FromArgb(30, 32, 36);
    public static readonly Color Text = Color.FromArgb(236, 236, 236);
    public static readonly Color SubText = Color.FromArgb(154, 160, 166);
    public static readonly Font UiFont = new("Segoe UI", 9.5f);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public static void ApplyDarkTitleBar(Form form)
    {
        try { int on = 1; DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)); }
        catch { /* unsupported OS */ }
    }

    private static Icon? _appIcon;
    public static Icon AppIcon
    {
        get
        {
            if (_appIcon is null) { using var b = BuildLogo(32); _appIcon = Icon.FromHandle(b.GetHicon()); }
            return _appIcon;
        }
    }

    public static void ApplyForm(Form form)
    {
        form.BackColor = Bg;
        form.ForeColor = Text;
        form.Font = UiFont;
        try { form.Icon = AppIcon; } catch { /* ignore */ }
        if (form.IsHandleCreated) ApplyDarkTitleBar(form);
        else form.HandleCreated += (_, _) => ApplyDarkTitleBar(form);
    }

    /// <summary>Draws the prism app icon: a glass triangle splitting a white beam into a rainbow.</summary>
    public static Bitmap BuildLogo(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float m = size * 0.14f;
        var tri = new[]
        {
            new PointF(size * 0.52f, m),
            new PointF(size - m, size - m),
            new PointF(m, size - m),
        };
        using (var fill = new LinearGradientBrush(new RectangleF(0, 0, size, size),
            Color.FromArgb(70, 130, 135, 255), Color.FromArgb(35, 150, 100, 210), 55f))
            g.FillPolygon(fill, tri);
        using (var pen = new Pen(Color.FromArgb(235, 205, 210, 255), Math.Max(1.4f, size * 0.03f)))
            g.DrawPolygon(pen, tri);

        using (var wp = new Pen(Color.FromArgb(245, 255, 255, 255), Math.Max(1.4f, size * 0.035f)))
            g.DrawLine(wp, new PointF(0, size * 0.5f), new PointF(size * 0.33f, size * 0.53f));

        Color[] rainbow =
        {
            Color.FromArgb(239, 68, 68), Color.FromArgb(245, 158, 11), Color.FromArgb(234, 179, 8),
            Color.FromArgb(34, 197, 94), Color.FromArgb(59, 130, 246), Color.FromArgb(139, 92, 246),
        };
        var origin = new PointF(size * 0.55f, size * 0.52f);
        for (int i = 0; i < rainbow.Length; i++)
        {
            using var rp = new Pen(rainbow[i], Math.Max(1.1f, size * 0.022f));
            float t = i / (float)(rainbow.Length - 1);
            g.DrawLine(rp, origin, new PointF(size * 0.99f, size * (0.40f + 0.52f * t)));
        }
        return bmp;
    }
}
