using System.Runtime.InteropServices;
using SkiaSharp;
using Vortice.Direct3D11;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace GameLearn;

/// <summary>One native session per source. Retains one GPU frame; CPU readback happens only on demand.</summary>
internal sealed class WgcCaptureSession : IDisposable
{
    private readonly object sync = new();
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly IDirect3DDevice winrtDevice;
    private readonly GraphicsCaptureItem item;
    private readonly Direct3D11CaptureFramePool pool;
    private readonly GraphicsCaptureSession session;
    private Direct3D11CaptureFrame? latest;
    private ID3D11Texture2D? staging;
    private SizeInt32 poolSize;
    private TaskCompletionSource signal = NewSignal();
    private Exception? failure;
    private bool closed;
    public nint Handle { get; }
    public bool IsClosed { get { lock (sync) return closed; } }
    public int ResizeCount { get; private set; }
    private int framesReceived, blankReadbacks;
    public string Describe() { lock (sync) return $"frames={framesReceived}, resize={ResizeCount}, pool={poolSize.Width}x{poolSize.Height}, latest={latest?.ContentSize.Width}x{latest?.ContentSize.Height}, blanks={blankReadbacks}, closed={closed}, error={failure?.Message}"; }

    public WgcCaptureSession(nint handle, ID3D11Device device, ID3D11DeviceContext context, IDirect3DDevice winrtDevice)
    {
        Handle = handle; this.device = device; this.context = context; this.winrtDevice = winrtDevice;
        item = Native.CreateCaptureItem(handle); poolSize = item.Size;
        pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, poolSize);
        try
        {
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            pool.FrameArrived += OnFrame;
            item.Closed += OnClosed;
            session.StartCapture();
        }
        catch
        {
            pool.FrameArrived -= OnFrame; item.Closed -= OnClosed;
            session?.Dispose(); pool.Dispose(); throw;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Pulse()
    {
        var previous = signal; signal = NewSignal(); previous.TrySetResult();
    }
    private void OnClosed(GraphicsCaptureItem sender, object args)
    {
        lock (sync) { failure = new InvalidOperationException("画面来源已关闭，请重新选择窗口。"); Pulse(); }
    }
    private void OnFrame(Direct3D11CaptureFramePool sender, object args)
    {
        lock (sync)
        {
            if (closed) return;
            Direct3D11CaptureFrame? next = null;
            try
            {
                next = sender.TryGetNextFrame();
                if (next is null) return;
                framesReceived++;
                var size = next.ContentSize;
                if (size.Width < 8 || size.Height < 8) return;
                if (size.Width != poolSize.Width || size.Height != poolSize.Height)
                {
                    latest?.Dispose(); latest = null; next.Dispose(); next = null;
                    staging?.Dispose(); staging = null; poolSize = size;
                    // Reallocate buffers, not the capture session: keep the border stable.
                    sender.Recreate(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                    // A static source may otherwise have no further invalidations after resize.
                    // Queue a repaint without synchronously calling into the source's UI thread.
                    RedrawWindow(Handle, 0, 0, 0x0001 | 0x0080);
                    ResizeCount++; Pulse(); return;
                }
                latest?.Dispose(); latest = next; next = null;
                Pulse();
            }
            catch (Exception error) { failure = error; Pulse(); }
            finally { next?.Dispose(); }
        }
    }

    public async Task<SKBitmap> SnapshotAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                Task changed;
                lock (sync)
                {
                    if (closed) throw new OperationCanceledException("捕获会话已停止。", token);
                    if (failure is not null) throw new InvalidOperationException("窗口捕获失败：" + failure.Message, failure);
                    if (latest is not null)
                    {
                        var bitmap = ReadPixels(latest);
                        if (!CaptureService.IsBlank(bitmap)) return bitmap;
                        blankReadbacks++;
                        bitmap.Dispose();
                    }
                    changed = signal.Task;
                }
                await changed.WaitAsync(deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("等待有效游戏画面超时，请确认来源没有最小化或黑屏。"); }
    }
    private SKBitmap ReadPixels(Direct3D11CaptureFrame frame)
    {
        using var texture = new ID3D11Texture2D(Native.GetTexture(frame.Surface));
        var desc = texture.Description;
        if (staging is null || staging.Description.Width != desc.Width || staging.Description.Height != desc.Height)
        {
            staging?.Dispose();
            desc.Usage = ResourceUsage.Staging; desc.BindFlags = BindFlags.None;
            desc.CPUAccessFlags = CpuAccessFlags.Read; desc.MiscFlags = ResourceOptionFlags.None;
            staging = device.CreateTexture2D(desc);
        }
        context.CopyResource(staging, texture);
        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        SKBitmap? bitmap = null;
        try
        {
            var width = Math.Min(frame.ContentSize.Width, (int)desc.Width);
            var height = Math.Min(frame.ContentSize.Height, (int)desc.Height);
            bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var row = new byte[width * 4];
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(mapped.DataPointer + (int)(y * mapped.RowPitch), row, 0, row.Length);
                Marshal.Copy(row, 0, bitmap.GetPixels() + y * bitmap.RowBytes, row.Length);
            }
            var result = bitmap; bitmap = null; return result;
        }
        finally { bitmap?.Dispose(); context.Unmap(staging, 0); }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (closed) return;
            closed = true; latest?.Dispose(); latest = null; staging?.Dispose(); staging = null; Pulse();
        }
        // Native Close may wait for a callback; never hold the callback lock here.
        pool.FrameArrived -= OnFrame; item.Closed -= OnClosed;
        session.Dispose(); pool.Dispose();
    }
    [DllImport("user32.dll")] private static extern bool RedrawWindow(nint hwnd, nint updateRect, nint updateRegion, uint flags);
}
