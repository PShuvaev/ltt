/// <summary>Which lines of a text differ from an earlier version (Myers' diff): the editor's change marks.</summary>
internal static class LineDiff
{
    public const byte Added = 1, Modified = 2, RemovedBelow = 4, RemovedAbove = 8;

    private const int ExactUpTo = 4000;    // a middle this long (both sides together) gets one exact diff
    private const int RunToMeet = 4;       // lines in a row that must agree for the texts to meet again
    private const int NearSteps = 64;      // how far the cheap look for that place goes
    private const int FarWindow = 1 << 16; // how far the indexed look goes
    private const long MaxWork = 4_000_000;

    /// <summary>
    /// A mark per line of `now` against `was`: added, modified, or with lines
    /// removed right below it (RemovedAbove — before the first line). A stretch
    /// that needs more than maxEdits edits is marked as one change. Equal lines
    /// take `now`'s string, so the next comparison is by reference.
    /// </summary>
    public static byte[] Marks(string[] was, IReadOnlyList<string> now, int maxEdits = 1000)
    {
        var marks = new byte[now.Count];
        int n = was.Length, m = now.Count, pre = 0, suf = 0;
        while (pre < n && pre < m && Same(was, pre, now[pre])) pre++;
        while (suf < n - pre && suf < m - pre && Same(was, n - 1 - suf, now[m - 1 - suf])) suf++;
        int iEnd = n - suf, jEnd = m - suf;
        if (iEnd - pre + jEnd - pre <= ExactUpTo)
        {
            MarkStretch(was, pre, iEnd - pre, now, pre, jEnd - pre, marks, maxEdits);
            return marks;
        }
        // A long middle (a big file changed in many places): walk it a line at a time; where the
        // texts part, find the nearest place they meet again and diff only the stretch between.
        // The cost follows the size of the changes, not of the file.
        for (int i = pre, j = pre; i < iEnd || j < jEnd;)
        {
            if (i < iEnd && j < jEnd && Same(was, i, now[j])) { i++; j++; continue; }
            var (i2, j2) = Meet(was, i, iEnd, now, j, jEnd);
            MarkStretch(was, i, i2 - i, now, j, j2 - j, marks, maxEdits);
            (i, j) = (i2, j2);
        }
        return marks;
    }

    /// <summary>
    /// Where lines of `was` are in `now`, by the diff Marks makes: a kept line where it went; one in a
    /// changed stretch at the same distance into what replaced it — or, past the end of that, at the
    /// line that follows it now (as for a stretch that went entirely; now.Count when that is past the end).
    /// </summary>
    public static int[] Follow(string[] was, IReadOnlyList<string> now, params int[] lines)
    {
        var marks = Marks(was, now);
        var kept = new List<(int Old, int New)>(); // the kept lines of now, matched in order to the lines of was they are
        for (int j = 0, p = 0; j < now.Count; j++)
        {
            if ((marks[j] & (Added | Modified)) != 0) continue;
            while (p < was.Length && !string.Equals(was[p], now[j], StringComparison.Ordinal)) p++;
            if (p == was.Length) break;
            kept.Add((p++, j));
        }
        var at = new int[lines.Length];
        for (int q = 0; q < lines.Length; q++)
        {
            int line = lines[q], k = kept.FindLastIndex(e => e.Old <= line);
            if (k >= 0 && kept[k].Old == line) { at[q] = kept[k].New; continue; }
            var (o1, n1) = k >= 0 ? kept[k] : (-1, -1);
            int n2 = k + 1 < kept.Count ? kept[k + 1].New : now.Count;
            at[q] = line - o1 < n2 - n1 ? n1 + (line - o1) : n2;
        }
        return at;
    }

    /// <summary>Marks for now[b0..b0+bn) against was[a0..a0+an): an exact diff, or — too costly — all changed.</summary>
    private static void MarkStretch(string[] was, int a0, int an, IReadOnlyList<string> now, int b0, int bn,
        byte[] marks, int maxEdits)
    {
        if (an == 0 && bn == 0) return;
        var (del, ins) = Script(was, a0, an, now, b0, bn, maxEdits)
                         ?? (Enumerable.Repeat(true, an).ToArray(), Enumerable.Repeat(true, bn).ToArray());
        // Walk both sides together: each run of removed and inserted lines is one change.
        for (int i = 0, j = 0; i < an || j < bn;)
        {
            if (i < an && j < bn && !del[i] && !ins[j]) { i++; j++; continue; }
            int i0 = i, j0 = j;
            while ((i < an && del[i]) || (j < bn && ins[j]))
            {
                if (i < an && del[i]) i++;
                if (j < bn && ins[j]) j++;
            }
            if (j > j0)
                for (int k = j0; k < j; k++) marks[b0 + k] = i > i0 ? Modified : Added;
            else if (b0 + j0 > 0) marks[b0 + j0 - 1] |= RemovedBelow;
            else if (marks.Length > 0) marks[0] |= RemovedAbove; // all gone: no line is left to mark
        }
    }

    /// <summary>
    /// The nearest (i2, j2) from which RunToMeet lines in a row agree (or all that
    /// is left does): first every split of a few steps, then an index of the lines
    /// ahead for longer stretches; failing both, the ends.
    /// </summary>
    private static (int, int) Meet(string[] was, int i, int iEnd, IReadOnlyList<string> now, int j, int jEnd)
    {
        for (int d = 1; d <= NearSteps; d++)
        {
            int mid = d / 2; // an even split first, then outwards to all-on-one-side
            for (int t = 0; t <= d - mid; t++)
                for (int sign = 1; sign >= -1; sign -= 2)
                {
                    int di = mid + sign * t, dj = d - di;
                    if ((t == 0 && sign < 0) || di < 0 || di > d) continue;
                    if (i + di <= iEnd && j + dj <= jEnd && Agree(was, i + di, iEnd, now, j + dj, jEnd))
                        return (i + di, j + dj);
                }
        }

        // Where each line of `was` ahead first shows up (a few places, for lines that repeat).
        var ahead = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int p = i; p < iEnd && p - i < FarWindow; p++)
        {
            if (!ahead.TryGetValue(was[p], out var at)) ahead[was[p]] = at = new List<int>(1);
            if (at.Count < 8) at.Add(p);
        }
        int best = int.MaxValue;
        var meet = (iEnd, jEnd);
        for (int dj = 0; dj < best && j + dj < jEnd && dj < FarWindow; dj++)
        {
            if (!ahead.TryGetValue(now[j + dj], out var at)) continue;
            foreach (int p in at)
            {
                if (p - i + dj >= best) break;
                if (!Agree(was, p, iEnd, now, j + dj, jEnd)) continue;
                (best, meet) = (p - i + dj, (p, j + dj));
                break;
            }
        }
        return meet;
    }

    /// <summary>Whether RunToMeet lines in a row agree from (i, j), or every line left does.</summary>
    private static bool Agree(string[] was, int i, int iEnd, IReadOnlyList<string> now, int j, int jEnd)
    {
        for (int k = 0; k < RunToMeet; k++, i++, j++)
        {
            if (i == iEnd || j == jEnd) return i == iEnd && j == jEnd;
            if (!Same(was, i, now[j])) return false;
        }
        return true;
    }

    private static bool Same(string[] a, int i, string s)
    {
        if (ReferenceEquals(a[i], s)) return true;
        if (!string.Equals(a[i], s, StringComparison.Ordinal)) return false;
        a[i] = s;
        return true;
    }

    /// <summary>The shortest edit script of a[a0..a0+n) into b[b0..b0+m): removed and inserted lines; null — over maxD edits or too much work.</summary>
    private static (bool[] Del, bool[] Ins)? Script(string[] a, int a0, int n, IReadOnlyList<string> b, int b0, int m, int maxD)
    {
        int max = Math.Min(n + m, maxD), off = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>(); // v as each step began, for k in [-d, d]
        long work = 0;
        for (int d = 0; d <= max; d++)
        {
            var snap = new int[2 * d + 1];
            Array.Copy(v, off - d, snap, 0, 2 * d + 1);
            trace.Add(snap);
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[off + k - 1] < v[off + k + 1]) ? v[off + k + 1] : v[off + k - 1] + 1;
                int y = x - k;
                int x0 = x;
                while (x < n && y < m && Same(a, a0 + x, b[b0 + y])) { x++; y++; }
                v[off + k] = x;
                if (x >= n && y >= m) return Backtrack(trace, n, m);
                if ((work += 1 + x - x0) > MaxWork) return null;
            }
        }
        return null;
    }

    private static (bool[], bool[]) Backtrack(List<int[]> trace, int n, int m)
    {
        var del = new bool[n];
        var ins = new bool[m];
        int x = n, y = m;
        for (int d = trace.Count - 1; d > 0; d--)
        {
            var v = trace[d];
            int k = x - y;
            int prevK = k == -d || (k != d && v[k - 1 + d] < v[k + 1 + d]) ? k + 1 : k - 1;
            int prevX = v[prevK + d], prevY = prevX - prevK;
            while (x > prevX && y > prevY) { x--; y--; } // the equal lines after the edit
            if (x == prevX) ins[prevY] = true;
            else del[prevX] = true;
            (x, y) = (prevX, prevY);
        }
        return (del, ins);
    }
}
