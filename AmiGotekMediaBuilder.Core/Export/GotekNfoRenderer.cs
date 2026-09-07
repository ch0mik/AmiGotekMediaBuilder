using System.Text;

namespace AmiGotekMediaBuilder.Core.Export;

public static class GotekNfoRenderer
{
    public const int MaxBytes = 512;
    private const string Ellipsis = "…";

    public static string Render(string? title, string? year = null, string? publisher = null, string? description = null)
    {
        title = string.IsNullOrWhiteSpace(title) ? "Unknown" : title.Trim();
        var blurb = string.Join(" - ", new[] { year, publisher, description }
            .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()));
        var full = $"Title: {title}\nBlurb:{(blurb.Length > 0 ? $" {blurb}" : "")}\n";
        if (Encoding.UTF8.GetByteCount(full) <= MaxBytes) return full;

        var prefix = $"Title: {title}\nBlurb: ";
        var available = MaxBytes - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(Ellipsis) - 1;
        if (available >= 0)
            return $"{prefix}{TakeUtf8(blurb, available)}{Ellipsis}\n";

        var titleAvailable = MaxBytes - Encoding.UTF8.GetByteCount("Title: ") -
            Encoding.UTF8.GetByteCount("\n") - Encoding.UTF8.GetByteCount(Ellipsis) -
            Encoding.UTF8.GetByteCount("Blurb:\n");
        if (titleAvailable < 0)
            return "Title: ";
        return $"Title: {TakeUtf8(title, titleAvailable)}{Ellipsis}\nBlurb:\n";
    }

    private static string TakeUtf8(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;
        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var character in value)
        {
            var count = Encoding.UTF8.GetByteCount(new[] { character });
            if (bytes + count > maxBytes) break;
            builder.Append(character);
            bytes += count;
        }
        return builder.ToString();
    }
}
