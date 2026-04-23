using System.Text;

namespace GuideAntsApi.Services;

public static class SlugGenerator
{
    public const int MaxSlugLength = 100;

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
    };

    public static string Generate(string? value, int maxLength = MaxSlugLength)
    {
        var input = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            input = "untitled";
        }

        var normalized = input.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(normalized.Length);
        var lastWasDash = false;

        foreach (var rune in normalized.EnumerateRunes())
        {
            // Drop diacritic marks so accented Latin characters become ASCII-friendly.
            if (Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (IsAsciiLetterOrDigit(rune))
            {
                sb.Append(char.ToLowerInvariant((char)rune.Value));
                lastWasDash = false;
                continue;
            }

            if (IsSeparatorLike(rune))
            {
                AppendDash(sb, ref lastWasDash);
                continue;
            }

            // Encode unsupported runes into ASCII-safe tokens.
            AppendDash(sb, ref lastWasDash);
            sb.Append('x');
            sb.Append(rune.Value.ToString("x"));
            sb.Append('x');
            AppendDash(sb, ref lastWasDash);
        }

        var slug = sb.ToString().Trim('-');

        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "untitled";
        }

        if (slug.Length > maxLength)
        {
            slug = slug[..maxLength].Trim('-');
        }

        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "untitled";
        }

        if (WindowsReservedNames.Contains(slug))
        {
            slug = $"{slug}-item";
        }

        return slug;
    }

    public static bool IsFilesystemSafeSlug(string? value, int maxLength = MaxSlugLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var slug = value.Trim();
        if (slug.Length == 0 || slug.Length > maxLength)
        {
            return false;
        }

        if (slug[0] == '-' || slug[^1] == '-' || slug.Contains("--", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var ch in slug)
        {
            if ((ch is >= 'a' and <= 'z') || (ch is >= '0' and <= '9') || ch == '-')
            {
                continue;
            }

            return false;
        }

        return !WindowsReservedNames.Contains(slug);
    }

    public static string DecodeEncodedSegments(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return string.Empty;
        }

        var segments = slug.Split('-', StringSplitOptions.None);
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length < 3 || segment[0] != 'x' || segment[^1] != 'x')
            {
                continue;
            }

            var hex = segment[1..^1];
            if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var value))
            {
                continue;
            }

            if (!Rune.IsValid(value))
            {
                continue;
            }

            segments[i] = char.ConvertFromUtf32(value);
        }

        return string.Join('-', segments);
    }

    public static string AddNumericSuffix(string baseSlug, int number, int maxLength = MaxSlugLength)
    {
        var suffix = $"-{number}";
        var trimTo = Math.Max(1, maxLength - suffix.Length);
        var trimmed = baseSlug.Length > trimTo ? baseSlug[..trimTo].Trim('-') : baseSlug;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            trimmed = "item";
        }

        return $"{trimmed}{suffix}";
    }

    private static bool IsAsciiLetterOrDigit(Rune rune)
    {
        return rune.Value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9';
    }

    private static bool IsSeparatorLike(Rune rune)
    {
        if (Rune.IsWhiteSpace(rune))
        {
            return true;
        }

        return rune.Value is '-' or '_' or '.' or '/' or '\\';
    }

    private static void AppendDash(StringBuilder sb, ref bool lastWasDash)
    {
        if (lastWasDash)
        {
            return;
        }

        sb.Append('-');
        lastWasDash = true;
    }
}
