namespace Storiva.Core.Listing;

/// <summary>Case-insensitive comparison that orders digit runs numerically ("file2" &lt; "file10").</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;

                var numX = x.AsSpan(startX, i - startX).TrimStart('0');
                var numY = y.AsSpan(startY, j - startY).TrimStart('0');
                if (numX.Length != numY.Length)
                {
                    return numX.Length.CompareTo(numY.Length);
                }

                var digits = numX.CompareTo(numY, StringComparison.Ordinal);
                if (digits != 0)
                {
                    return digits;
                }
            }
            else
            {
                var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0)
                {
                    return c;
                }

                i++;
                j++;
            }
        }

        var lengthOrder = (x.Length - i).CompareTo(y.Length - j);
        return lengthOrder != 0 ? lengthOrder : string.CompareOrdinal(x, y);
    }
}
