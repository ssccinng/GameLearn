using SkiaSharp;

namespace GameLearn;

public static class TextRegion
{
    public static SKRectI? Around(IReadOnlyList<RecognizedLine> lines, CapturedFrame frame)
    {
        var text = lines.Where(l => l.Words.Any() && l.Confidence >= .8).ToArray();
        if (text.Length == 0 || text.Length != lines.Count(l => l.Words.Any())) return null;
        var pad = Math.Max(32, text.Max(l => l.Bounds.Height) * 1.5);
        var left = Math.Max(0, (int)Math.Floor(text.Min(l => l.Bounds.X) - frame.Region.X - pad));
        var top = Math.Max(0, (int)Math.Floor(text.Min(l => l.Bounds.Y) - frame.Region.Y - pad));
        var right = Math.Min((int)frame.Region.Width, (int)Math.Ceiling(text.Max(l => l.Bounds.X + l.Bounds.Width) - frame.Region.X + pad));
        var bottom = Math.Min((int)frame.Region.Height, (int)Math.Ceiling(text.Max(l => l.Bounds.Y + l.Bounds.Height) - frame.Region.Y + pad));
        if (right - left < 8 || bottom - top < 8 || (right - left) * (double)(bottom - top) > frame.Region.Width * frame.Region.Height * .7) return null;
        return new(left, top, right, bottom);
    }
}
