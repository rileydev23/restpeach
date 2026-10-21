namespace RestPeach.Core.Text;

/// <summary>
/// "2. Users" before "10. Webhooks": runs of digits compare as numbers, the rest ignoring case. Collections exported
/// from Postman number their folders, and the tree (and the order of a run) should follow those numbers.
/// </summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                if (a.Length != b.Length) return a.Length - b.Length;
                int c = string.CompareOrdinal(a, b);
                if (c != 0) return c;
                continue;
            }
            int d = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (d != 0) return d;
            i++;
            j++;
        }
        int rest = (x.Length - i) - (y.Length - j);
        return rest != 0 ? rest : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
    }
}
