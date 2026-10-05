using System.Globalization;
using System.Text;

namespace AmiGotekMediaBuilder.Core.Metadata;

internal static class GameTitleMatcher
{
    public static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var clean = new string(decomposed.Where(character =>
            CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray());
        var tokens = clean.ToLowerInvariant().Replace('&', ' ').Split(
            [' ', '\t', '\r', '\n', '-', '_', ':', '/', '(', ')', '[', ']', ',', '.', '\'', '"', '!'],
            StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', tokens.OrderBy(token => token, StringComparer.Ordinal));
    }

    public static double Score(string left, string right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a.Equals(b, StringComparison.Ordinal)) return 1;
        var distance = Levenshtein(a, b);
        return 1d - (double)distance / Math.Max(a.Length, b.Length);
    }

    private static int Levenshtein(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
                current[column] = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }
}
