using System.Text.RegularExpressions;

namespace GameLearn;

/// <summary>Conservative, frame-local layout repair. Never infers missing words.</summary>
public static class SentenceAssembler
{
    public static IReadOnlyList<RecognizedLine> Merge(IEnumerable<RecognizedLine> input)
    {
        var rows = new List<RecognizedLine>();
        foreach (var line in input.OrderBy(l => l.Bounds.Y).ThenBy(l => l.Bounds.X))
        {
            var index = rows.FindLastIndex(left => SameRow(left, line));
            if (index >= 0) rows[index] = rows[index].Bounds.X <= line.Bounds.X ? Join(rows[index], line) : Join(line, rows[index]);
            else rows.Add(line);
        }
        var groups = new List<RecognizedLine>();
        var consumed = new HashSet<int>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (!consumed.Add(i)) continue;
            var group = rows[i];
            var tail = rows[i];
            for (var j = i + 1; j < rows.Count; j++)
            {
                if (consumed.Contains(j) || !Continuation(tail, rows[j])) continue;
                group = Join(group, rows[j]); tail = rows[j]; consumed.Add(j);
            }
            groups.Add(group);
        }
        return groups;
    }

    private static bool SimilarHeight(PixelRect a, PixelRect b) =>
        a.Height > 0 && b.Height > 0 && Math.Max(a.Height, b.Height) / Math.Min(a.Height, b.Height) <= 1.35;

    private static bool SameRow(RecognizedLine a, RecognizedLine b)
    {
        var x = a.Bounds; var y = b.Bounds;
        if (x.X > y.X) (x, y) = (y, x);
        var gap = y.X - x.X - x.Width;
        return SimilarHeight(x, y) && Math.Abs(x.Y + x.Height / 2 - y.Y - y.Height / 2) <= Math.Min(x.Height, y.Height) * .3
            && gap >= -2 && gap <= Math.Min(x.Height, y.Height) * .8;
    }

    private static bool Continuation(RecognizedLine a, RecognizedLine b)
    {
        var x = a.Bounds; var y = b.Bounds;
        var gap = y.Y - x.Y - x.Height;
        // Short labels, menu rows and completed sentences are not wrapped dialogue.
        if (!SimilarHeight(x, y) || gap < 0 || gap > Math.Min(x.Height, y.Height) * .8
            || a.Words.Count() < 3 || Regex.IsMatch(a.Text.TrimEnd(), "[.!?:…][\\\"'’”)]*$")) return false;
        var leftAligned = Math.Abs(x.X - y.X) <= x.Height * 1.5;
        var centered = Math.Abs(x.X + x.Width / 2 - y.X - y.Width / 2) <= x.Height;
        var overlap = Math.Min(x.X + x.Width, y.X + y.Width) - Math.Max(x.X, y.X);
        return (leftAligned || centered) && overlap >= Math.Min(x.Width, y.Width) * .65;
    }

    private static RecognizedLine Join(RecognizedLine a, RecognizedLine b)
    {
        var left = Math.Min(a.Bounds.X, b.Bounds.X); var top = Math.Min(a.Bounds.Y, b.Bounds.Y);
        var right = Math.Max(a.Bounds.X + a.Bounds.Width, b.Bounds.X + b.Bounds.Width);
        var bottom = Math.Max(a.Bounds.Y + a.Bounds.Height, b.Bounds.Y + b.Bounds.Height);
        var fragments = (a.Fragments ?? new[] { a }).Concat(b.Fragments ?? new[] { b }).ToArray();
        return new(a.Text.TrimEnd() + " " + b.Text.TrimStart(), Math.Min(a.Confidence, b.Confidence), new(left, top, right - left, bottom - top)) { Fragments = fragments };
    }
}
