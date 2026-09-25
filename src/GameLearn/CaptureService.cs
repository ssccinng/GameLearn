using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace GameLearn;

public sealed class CaptureService : IDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private ID3D11Device? device;
    private ID3D11DeviceContext? context;
    private IDirect3DDevice? winrtDevice;
    private WgcCaptureSession? activeSession;
    private volatile bool continuous;
    public string LastBackend { get; private set; } = "";
    public int WgcSessionsStarted { get; private set; }
    public int WgcSessionsStopped { get; private set; }
    public bool HasActiveSession => activeSession is not null;
    private string previousSessionState = "";
    internal string DebugState => activeSession?.Describe() ?? previousSessionState;

    public Task SetContinuousAsync(bool enabled)
    {
        continuous = enabled;
        return enabled ? Task.CompletedTask : ResetAsync();
    }
    public async Task ResetAsync()
    {
        await gate.WaitAsync();
        try { CloseSession(); }
        finally { gate.Release(); }
    }
    private void CloseSession()
    {
        if (activeSession is null) return;
        previousSessionState = activeSession.Describe();
        activeSession.Dispose(); activeSession = null; WgcSessionsStopped++;
    }

    public static IReadOnlyList<WindowSource> ListWindows()
    {
        var list = new List<WindowSource>();
        Native.EnumWindows((handle, _) =>
        {
            if (!Native.IsWindowVisible(handle) || Native.IsIconic(handle)) return true;
            var title = new StringBuilder(1024);
            Native.GetWindowText(handle, title, title.Capacity);
            Native.GetWindowThreadProcessId(handle, out var pid);
            if (title.Length == 0 || pid == Environment.ProcessId) return true;
            try { list.Add(new(handle, title.ToString(), Process.GetProcessById((int)pid).ProcessName)); } catch (ArgumentException) { }
            return true;
        }, 0);
        return list.OrderByDescending(w => w.IsObsProjector)
            .ThenByDescending(w => w.ProcessName == "obs64").ThenBy(w => w.Title).ToArray();
    }

    public async Task<CapturedFrame> CaptureAsync(WindowSource source, string game, CropRegion? crop, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            Validate(source);
            SKBitmap? bitmap = null;
            var clientOnly = false;
            var bounds = CaptureGeometry.Read(source.Handle, false);
            if (source.ProcessName.Equals("obs64", StringComparison.OrdinalIgnoreCase))
            {
                // OBS renders correctly through PrintWindow. Avoid a capture indicator entirely.
                try
                {
                    bounds = CaptureGeometry.Read(source.Handle, true);
                    bitmap = await Task.Run(() => CapturePrintWindow(source.Handle), token);
                    if (IsBlank(bitmap)) { bitmap.Dispose(); bitmap = null; }
                    else { CloseSession(); LastBackend = "OBS · PrintWindow"; clientOnly = true; }
                }
                catch (Exception error) when (error is not OperationCanceledException) { bitmap?.Dispose(); bitmap = null; }
            }
            if (bitmap is null)
            {
                bounds = CaptureGeometry.Read(source.Handle, false);
                bitmap = await CaptureWgcAsync(source.Handle, token);
                LastBackend = "Windows Graphics Capture";
            }
            using (bitmap)
            {
                token.ThrowIfCancellationRequested(); Validate(source);
                if (IsBlank(bitmap)) throw new InvalidOperationException("捕获到黑画面，请确认游戏来源有信号且 OBS 预览已启用。");
                if (bounds is null || bounds != CaptureGeometry.Read(source.Handle, clientOnly)
                    || Math.Abs(bounds.Width - bitmap.Width) > 2 || Math.Abs(bounds.Height - bitmap.Height) > 2)
                    throw new InvalidOperationException("窗口正在移动或缩放，请待画面稳定后重新识别。");
                var frame = await Task.Run(() => CreateFrame(bitmap, game, source.Title, crop), token);
                return frame with { DesktopBounds = bounds, CapturedClientOnly = clientOnly, SourceKey = source.Key };
            }
        }
        catch { CloseSession(); throw; }
        finally { if (!continuous) CloseSession(); gate.Release(); }
    }

    private static void Validate(WindowSource source)
    {
        if (!Native.IsWindow(source.Handle) || Native.IsIconic(source.Handle))
            throw new InvalidOperationException("画面来源已关闭或最小化，请恢复窗口并重新选择。");
    }

    public static CapturedFrame CreateFrame(SKBitmap bitmap, string game, string source, CropRegion? crop = null)
    {
        var rect = crop is null ? new SKRectI(0, 0, bitmap.Width, bitmap.Height) : new SKRectI(
            (int)(crop.X * bitmap.Width), (int)(crop.Y * bitmap.Height),
            (int)((crop.X + crop.Width) * bitmap.Width), (int)((crop.Y + crop.Height) * bitmap.Height));
        rect = SKRectI.Intersect(rect, new SKRectI(0, 0, bitmap.Width, bitmap.Height));
        if (rect.Width < 8 || rect.Height < 8) throw new InvalidOperationException("识别区域太小，请重新框选。");
        using var region = new SKBitmap(rect.Width, rect.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(region)) canvas.DrawBitmap(bitmap, rect, new SKRect(0, 0, rect.Width, rect.Height));
        using var full = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var partial = region.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = partial.ToArray();
        return new(Guid.NewGuid(), DateTimeOffset.Now, game, source, full.ToArray(), bytes, bitmap.Width, bitmap.Height,
            new(rect.Left, rect.Top, rect.Width, rect.Height), Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private async Task<SKBitmap> CaptureWgcAsync(nint handle, CancellationToken token)
    {
        if (!GraphicsCaptureSession.IsSupported()) throw new InvalidOperationException("系统不支持窗口捕获。");
        if (device is null)
        {
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 }, out device, out context).CheckError();
            using var dxgi = device.QueryInterface<IDXGIDevice>();
            Marshal.ThrowExceptionForHR(Native.CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var ptr));
            try { winrtDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(ptr); }
            finally { Marshal.Release(ptr); }
        }
        if (activeSession is null || activeSession.Handle != handle || activeSession.IsClosed)
        {
            CloseSession();
            activeSession = new WgcCaptureSession(handle, device, context!, winrtDevice!);
            WgcSessionsStarted++;
        }
        return await activeSession.SnapshotAsync(token);
    }

    public static SKBitmap CapturePrintWindow(nint handle)
    {
        var previous = Native.SetThreadDpiAwarenessContext(-4);
        try
        {
            Native.GetClientRect(handle, out var rect);
            using var bitmap = new System.Drawing.Bitmap(rect.Right, rect.Bottom, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            var dc = graphics.GetHdc();
            bool success;
            try { success = Native.PrintWindow(handle, dc, 3); } finally { graphics.ReleaseHdc(dc); }
            if (!success) throw new InvalidOperationException("OBS 窗口捕获失败，请保持投影窗口打开。");
            using var stream = new MemoryStream(); bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return SKBitmap.Decode(stream.ToArray());
        }
        finally { Native.SetThreadDpiAwarenessContext(previous); }
    }
    internal static bool IsBlank(SKBitmap bitmap)
    {
        var brightest = 0;
        for (var y = 0; y < bitmap.Height; y += Math.Max(1, bitmap.Height / 40))
        for (var x = 0; x < bitmap.Width; x += Math.Max(1, bitmap.Width / 40))
        {
            var c = bitmap.GetPixel(x, y); brightest = Math.Max(brightest, Math.Max(c.Red, Math.Max(c.Green, c.Blue)));
        }
        return brightest < 12;
    }
    public void Dispose() { CloseSession(); winrtDevice?.Dispose(); context?.Dispose(); device?.Dispose(); gate.Dispose(); }
}

internal static class Native
{
    internal delegate bool EnumProc(nint handle, nint parameter);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint handle, StringBuilder text, int count);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint handle, out RECT rect);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint handle, ref POINT point);
    [DllImport("user32.dll")] internal static extern bool PrintWindow(nint handle, nint dc, uint flags);
    [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint value);
    [DllImport("d3d11.dll")] internal static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgi, out nint device);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, int length, out nint hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint hstring);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint hstring, ref Guid iid, out nint factory);
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
    internal static unsafe GraphicsCaptureItem CreateCaptureItem(nint handle)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var str));
        nint factory = 0, item = 0;
        try
        {
            var factoryId = new Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(str, ref factoryId, out factory));
            var iid = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            var vtable = *(nint**)factory;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)vtable[3])(factory, handle, &iid, &item));
            return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
        }
        finally { if (item != 0) Marshal.Release(item); if (factory != 0) Marshal.Release(factory); WindowsDeleteString(str); }
    }
    internal static unsafe nint GetTexture(IDirect3DSurface surface)
    {
        var ptr = WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        nint access = 0;
        try
        {
            var iid = new Guid("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(ptr, in iid, out access));
            var textureId = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
            nint texture;
            var vtable = *(nint**)access;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[3])(access, &textureId, &texture));
            return texture;
        }
        finally { if (access != 0) Marshal.Release(access); Marshal.Release(ptr); }
    }
}

