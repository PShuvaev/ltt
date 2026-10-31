/// <summary>Exact, case-sensitive, non-overlapping matches for replacement (unlike fuzzy navigation).</summary>
internal static class LiteralSearch
{
    public static IEnumerable<(int Start, int Length)> InLine(string text, string query)
    {
        if (query.Length == 0) yield break;
        int at = 0;
        while ((at = text.IndexOf(query, at, StringComparison.Ordinal)) >= 0)
        {
            yield return (at, query.Length);
            at += query.Length;
        }
    }

    public static string ReplaceLine(string text, string query, string replacement, out int count)
    {
        count = 0;
        if (query.Length == 0) return text;
        int at = text.IndexOf(query, StringComparison.Ordinal);
        if (at < 0) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        int copied = 0;
        do
        {
            sb.Append(text, copied, at - copied).Append(replacement);
            copied = at + query.Length;
            count++;
            at = text.IndexOf(query, copied, StringComparison.Ordinal);
        } while (at >= 0);
        return sb.Append(text, copied, text.Length - copied).ToString();
    }
}
