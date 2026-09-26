using System.Runtime.InteropServices;
using SkiaSharp;

namespace GameLearn;

/// <summary>Managed snapshot ownership, independent of the native capture bitmap lifetime.</summary>
public sealed record CapturedPixels(SKImageInfo Info, int RowBytes, byte[] Data)
{
    public static CapturedPixels Copy(SKBitmap bitmap) => new(bitmap.Info, bitmap.RowBytes, bitmap.Bytes);
    public SKBitmap Open()
    {
        var bitmap = new SKBitmap();
        var pin = GCHandle.Alloc(Data, GCHandleType.Pinned);
        if (!bitmap.InstallPixels(Info, pin.AddrOfPinnedObject(), RowBytes, (_, _) => { if (pin.IsAllocated) pin.Free(); }, null))
        { if (pin.IsAllocated) pin.Free(); bitmap.Dispose(); throw new InvalidDataException("无法读取捕获像素。"); }
        return bitmap;
    }
}
