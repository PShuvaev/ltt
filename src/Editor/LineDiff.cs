/// <summary>Which lines of a text differ from an earlier version (Myers' diff): the editor's change marks.</summary>
internal static class LineDiff
{
    public const byte Added = 1, Modified = 2, RemovedBelow = 4, RemovedAbove = 8;

    /// <summary>
    /// A mark per line of `now` against `was`: added, modified, or with lines
    /// removed right below it (RemovedAbove — before the first line). A middle
    /// that needs more than maxEdits edits is marked as one change. Equal lines
    /// take `now`'s string, so the next comparison is by reference.
    /// </summary>
    public static byte[] Marks(string[] was, IReadOnlyList<string> now, int maxEdits = 1000)
    {
        var marks = new byte[now.Count];
        int n = was.Length, m = now.Count, pre = 0, suf = 0;
        while (pre < n && pre < m && Same(was, pre, now[pre])) pre++;
        while (suf < n - pre && suf < m - pre && Same(was, n - 1 - suf, now[m - 1 - suf])) suf++;
        int an = n - pre - suf, bn = m - pre - suf;
        if (an == 0 && bn == 0) return marks;
        var (del, ins) = Script(was, pre, an, now, pre, bn, maxEdits)
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
                for (int k = j0; k < j; k++) marks[pre + k] = i > i0 ? Modified : Added;
            else if (pre + j0 > 0) marks[pre + j0 - 1] |= RemovedBelow;
            else marks[0] |= RemovedAbove;
        }
        return marks;
    }

    private static bool Same(string[] a, int i, string s)
    {
        if (ReferenceEquals(a[i], s)) return true;
        if (!string.Equals(a[i], s, StringComparison.Ordinal)) return false;
        a[i] = s;
        return true;
    }

    /// <summary>The shortest edit script of a[a0..a0+n) into b[b0..b0+m): removed and inserted lines; null — over maxD edits.</summary>
    private static (bool[] Del, bool[] Ins)? Script(string[] a, int a0, int n, IReadOnlyList<string> b, int b0, int m, int maxD)
    {
        int max = Math.Min(n + m, maxD), off = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>(); // v as each step began, for k in [-d, d]
        for (int d = 0; d <= max; d++)
        {
            var snap = new int[2 * d + 1];
            Array.Copy(v, off - d, snap, 0, 2 * d + 1);
            trace.Add(snap);
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[off + k - 1] < v[off + k + 1]) ? v[off + k + 1] : v[off + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && Same(a, a0 + x, b[b0 + y])) { x++; y++; }
                v[off + k] = x;
                if (x >= n && y >= m) return Backtrack(trace, n, m);
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
