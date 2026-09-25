using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace GameLearn;

public static class FloatingPlacement
{
    public static PixelRect Clamp(PixelRect rectangle, IReadOnlyList<PixelRect> workAreas)
    {
        if (workAreas.Count == 0) return rectangle;
        static double Distance(PixelRect r, PixelRect area)
        {
            var x = r.X + r.Width / 2; var y = r.Y + r.Height / 2;
            return Math.Pow(x - Math.Clamp(x, area.X, area.X + area.Width), 2) + Math.Pow(y - Math.Clamp(y, area.Y, area.Y + area.Height), 2);
        }
        var work = workAreas.OrderBy(area => Distance(rectangle, area)).First();
        var width = Math.Min(rectangle.Width, work.Width); var height = Math.Min(rectangle.Height, work.Height);
        return new(Math.Clamp(rectangle.X, work.X, work.X + work.Width - width),
            Math.Clamp(rectangle.Y, work.Y, work.Y + work.Height - height), width, height);
    }
    internal static PixelRect WindowBounds(Window window)
    {
        GetWindowRect(new WindowInteropHelper(window).Handle, out var r);
        return new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }
    internal static IReadOnlyList<PixelRect> WorkAreas() => Forms.Screen.AllScreens.Select(s =>
        new PixelRect(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height)).ToArray();
    internal static void MoveIntoView(Window window, double? x = null, double? y = null)
    {
        var bounds = WindowBounds(window);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        if (x is { } left && double.IsFinite(left)) bounds = bounds with { X = left };
        if (y is { } top && double.IsFinite(top)) bounds = bounds with { Y = top };
        var placed = Clamp(bounds, WorkAreas());
        if (placed.Width < bounds.Width || placed.Height < bounds.Height)
        {
            var scale = GetDpiForWindow(new WindowInteropHelper(window).Handle) / 96.0;
            if (scale > 0)
            {
                window.MinWidth = Math.Min(window.MinWidth, placed.Width / scale);
                window.MinHeight = Math.Min(window.MinHeight, placed.Height / scale);
                window.MaxWidth = placed.Width / scale; window.MaxHeight = placed.Height / scale;
                window.UpdateLayout();
            }
        }
        SetWindowPos(new WindowInteropHelper(window).Handle, 0, (int)placed.X, (int)placed.Y, 0, 0, 0x0001 | 0x0004 | 0x0010);
    }
    internal static void PlaceCard(Window card, Window toolbar)
    {
        var anchor = WindowBounds(toolbar); var rect = WindowBounds(card);
        var area = WorkAreas().OrderBy(a => Math.Abs(a.X + a.Width / 2 - anchor.X - anchor.Width / 2)
            + Math.Abs(a.Y + a.Height / 2 - anchor.Y - anchor.Height / 2)).First();
        var handle = new WindowInteropHelper(card).Handle;
        var scale = GetDpiForWindow(handle) / 96.0;
        if (scale <= 0) scale = 1;
        var maximumHeight = Math.Max(200, (area.Height - 24) / scale);
        card.MinHeight = Math.Min(card.MinHeight, maximumHeight);
        card.MaxHeight = maximumHeight; card.Height = Math.Min(card.Height, maximumHeight);
        card.UpdateLayout(); rect = WindowBounds(card);
        var top = anchor.Y - rect.Height - 10;
        if (top < area.Y) top = anchor.Y + anchor.Height + 10;
        MoveIntoView(card, anchor.X + anchor.Width - rect.Width, top);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rectangle);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
