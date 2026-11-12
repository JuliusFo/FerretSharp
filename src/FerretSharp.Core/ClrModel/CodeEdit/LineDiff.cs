namespace FerretSharp.Core.ClrModel;

/// <summary>How a line of a diff differs.</summary>
public enum DiffLineKind
{
    Same,
    Removed,
    Added,
}

/// <summary>A line of a diff, with its number in the old and in the new text (null where it is not there).</summary>
public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldNumber, int? NewNumber);

/// <summary>A block of changed lines with some lines around them.</summary>
public sealed record DiffHunk(IReadOnlyList<DiffLine> Lines);

/// <summary>
/// The lines two texts differ in, for the preview of a change (WP-41): Myers' algorithm on whole lines, grouped into
/// hunks with <see cref="Context"/> unchanged lines around each. Monaco's diff editor is not used – it needs a worker the
/// trimmed build does not have.
/// </summary>
public static class LineDiff
{
    public const int Context = 3;

    /// <summary>The hunks; empty if the texts have the same lines (line endings do not count).</summary>
    public static IReadOnlyList<DiffHunk> Of(string? before, string after)
    {
        var a = Lines(before ?? "");
        var b = Lines(after);
        if (a.Length == 0)
        {
            return b.Length == 0 ? [] : [new DiffHunk([.. b.Select((line, i) => new DiffLine(DiffLineKind.Added, line, null, i + 1))])];
        }

        var script = Script(a, b);
        var hunks = new List<DiffHunk>();
        var i = 0;
        while (i < script.Count)
        {
            if (script[i].Kind == DiffLineKind.Same)
            {
                i++;
                continue;
            }

            var start = Math.Max(0, i - Context);
            var end = i;
            // one hunk while changes are at most 2 × Context lines apart
            while (true)
            {
                while (end < script.Count && script[end].Kind != DiffLineKind.Same)
                {
                    end++;
                }

                var next = end;
                while (next < script.Count && script[next].Kind == DiffLineKind.Same)
                {
                    next++;
                }

                if (next < script.Count && next - end <= 2 * Context)
                {
                    end = next;
                    continue;
                }

                break;
            }

            var stop = Math.Min(script.Count, end + Context);
            hunks.Add(new DiffHunk(script.GetRange(start, stop - start)));
            i = stop;
        }

        return hunks;
    }

    private static string[] Lines(string text) => text.Length == 0 ? [] : text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');

    /// <summary>The edit script of Myers' O(ND) algorithm: every line of both texts, in order.</summary>
    private static List<DiffLine> Script(string[] a, string[] b)
    {
        var n = a.Length;
        var m = b.Length;
        var max = n + m;
        var v = new int[2 * max + 2];
        var trace = new List<int[]>();
        var done = false;
        for (var d = 0; d <= max && !done; d++)
        {
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[max + k - 1] < v[max + k + 1]) ? v[max + k + 1] : v[max + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }

                v[max + k] = x;
                if (x >= n && y >= m)
                {
                    done = true;
                    break;
                }
            }
        }

        // Walk back from the end.
        var script = new List<DiffLine>();
        var (px, py) = (n, m);
        for (var d = trace.Count - 1; d >= 0 && (px > 0 || py > 0); d--)
        {
            var vd = trace[d];
            var k = px - py;
            var prevK = k == -d || (k != d && vd[max + k - 1] < vd[max + k + 1]) ? k + 1 : k - 1;
            var prevX = d == 0 ? 0 : vd[max + prevK];
            var prevY = prevX - prevK;
            while (px > prevX && py > prevY)
            {
                px--;
                py--;
                script.Add(new DiffLine(DiffLineKind.Same, a[px], px + 1, py + 1));
            }

            if (d > 0)
            {
                if (px == prevX)
                {
                    py--;
                    script.Add(new DiffLine(DiffLineKind.Added, b[py], null, py + 1));
                }
                else
                {
                    px--;
                    script.Add(new DiffLine(DiffLineKind.Removed, a[px], px + 1, null));
                }
            }
        }

        script.Reverse();
        return script;
    }
}
