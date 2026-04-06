using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Azure.PIM.Tray.Services;

/// <summary>
/// Generates distinct coloured circle icons for each window type.
/// </summary>
internal static class WindowIconHelper
{
    public static void SetIcon(Window window, Color circleColor, string letter)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(circleColor);
            g.FillEllipse(brush, 1, 1, 29, 29);
            using var font = new Font("Segoe UI", 14f, System.Drawing.FontStyle.Bold);
            var size = g.MeasureString(letter, font);
            g.DrawString(letter, font, Brushes.White,
                (30 - size.Width) / 2 + 1,
                (30 - size.Height) / 2 + 1);
        }

        var hBitmap = bmp.GetHbitmap();
        try
        {
            window.Icon = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    // Standard colours per window purpose
    public static void ApplyApprovalIcon(Window w)   => SetIcon(w, Color.FromArgb(0x00, 0x99, 0x44), "\u2713"); // green checkmark
    public static void ApplyActivateIcon(Window w)   => SetIcon(w, Color.FromArgb(0x00, 0x66, 0xCC), "\u26a1"); // blue lightning
    public static void ApplyLogViewerIcon(Window w)  => SetIcon(w, Color.FromArgb(0x66, 0x66, 0x99), "\u2261"); // grey-purple list
    public static void ApplyManageIcon(Window w)     => SetIcon(w, Color.FromArgb(0x88, 0x44, 0xCC), "\u2699"); // purple gear
    public static void ApplyRefreshIcon(Window w)    => SetIcon(w, Color.FromArgb(0x00, 0x66, 0xCC), "\u21ba"); // blue refresh

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
