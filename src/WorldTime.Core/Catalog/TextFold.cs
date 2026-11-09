using System.Globalization;
using System.Text;

namespace WorldTime.Core.Catalog;

/// <summary>Search normalization: lower-case ASCII, accents removed, punctuation to spaces.</summary>
public static class TextFold
{
    public static string Fold(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = s.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(d.Length);
        bool space = true; // collapse runs, trim start
        foreach (var ch in d)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.SpacingCombiningMark) continue;
            string? mapped = ch switch
            {
                'ß' => "ss", 'ø' or 'Ø' => "o", 'ł' or 'Ł' => "l", 'đ' or 'Đ' => "d",
                'æ' or 'Æ' => "ae", 'œ' or 'Œ' => "oe", 'ı' => "i", 'þ' or 'Þ' => "th",
                '\'' or '’' or '`' => "", // "N'Djamena" -> "ndjamena"
                _ => null,
            };
            if (mapped is not null)
            {
                sb.Append(mapped);
                if (mapped.Length > 0) space = false;
                continue;
            }
            char c = char.ToLowerInvariant(ch);
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(c);
                space = false;
            }
            else if (!space)
            {
                sb.Append(' ');
                space = true;
            }
        }
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }

    /// <summary>Optimal string alignment distance, stopping early above <paramref name="max"/>.</summary>
    public static int Distance(ReadOnlySpan<char> a, ReadOnlySpan<char> b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        int n = a.Length, m = b.Length;
        Span<int> prev2 = stackalloc int[m + 1];
        Span<int> prev = stackalloc int[m + 1];
        Span<int> cur = stackalloc int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;
        for (int i = 1; i <= n; i++)
        {
            cur[0] = i;
            int rowMin = cur[0];
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int v = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    v = Math.Min(v, prev2[j - 2] + 1);
                cur[j] = v;
                if (v < rowMin) rowMin = v;
            }
            if (rowMin > max) return max + 1;
            var t = prev2; prev2 = prev; prev = cur; cur = t;
        }
        return prev[m];
    }
}
