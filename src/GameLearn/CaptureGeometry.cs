using System.Runtime.InteropServices;

namespace GameLearn;

public static class CaptureGeometry
{
    public static PixelRect? Read(nint handle, bool clientOnly)
    {
        if (!Native.IsWindow(handle) || !Native.IsWindowVisible(handle) || Native.IsIconic(handle)) return null;
        if (clientOnly)
        {
            if (!Native.GetClientRect(handle, out var client)) return null;
            var origin = new Native.POINT();
            if (!Native.ClientToScreen(handle, ref origin)) return null;
            return new(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
        }
        if (DwmGetWindowAttribute(handle, 9, out var bounds, Marshal.SizeOf<Native.RECT>()) != 0
            && !GetWindowRect(handle, out bounds)) return null;
        return new(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
    }
    // Black bars are pixels in the projector snapshot, so no additional aspect-ratio offset is applied.
    public static PixelRect Map(PixelRect box, int frameWidth, int frameHeight, double width, double height)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(frameWidth));
        return new(box.X * width / frameWidth, box.Y * height / frameHeight, box.Width * width / frameWidth, box.Height * height / frameHeight);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, int attribute, out Native.RECT rect, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out Native.RECT rect);
}
