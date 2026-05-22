using System.Text;

namespace GankedTV.Api.Services.Tags;

/// <summary>
/// Slug normalization for user-submitted tag input. Used both by the resolver
/// (to canonicalize before get-or-create) and by the autocomplete endpoint
/// (to canonicalize the <c>prefix</c> query before ILIKE).
/// </summary>
public static class TagNormalization
{
    public const int MinLength = 2;
    public const int MaxLength = 24;
    public const int MaxTagsPerClip = 5;

    /// <summary>
    /// Normalize a single raw input to a slug: lowercased, internal whitespace + non-alnum
    /// → <c>-</c>, repeated hyphens collapsed, edge hyphens trimmed. Returns <c>false</c>
    /// if the result is empty or outside <see cref="MinLength"/>..<see cref="MaxLength"/>.
    /// </summary>
    public static bool TryNormalize(string? raw, out string slug)
    {
        slug = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var sb = new StringBuilder(raw.Length);
        var lastWasHyphen = false;
        foreach (var ch in raw)
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(ch);
                lastWasHyphen = false;
            }
            else if (ch is >= 'A' and <= 'Z')
            {
                sb.Append((char)(ch + ('a' - 'A')));
                lastWasHyphen = false;
            }
            else if (ch == '-' || ch == '_' || char.IsWhiteSpace(ch))
            {
                if (sb.Length == 0 || lastWasHyphen) continue;
                sb.Append('-');
                lastWasHyphen = true;
            }
            // Any other character is silently dropped — emoji, punctuation, accents etc.
        }

        // Trim trailing hyphen if any.
        if (sb.Length > 0 && sb[^1] == '-') sb.Length--;

        if (sb.Length < MinLength || sb.Length > MaxLength) return false;

        slug = sb.ToString();
        return true;
    }

    /// <summary>
    /// Normalize a prefix for autocomplete LIKE queries. Lowercases and strips disallowed
    /// characters but does NOT enforce min length (so a one-character prefix like "c" is
    /// still queryable). Returns <c>null</c> if the prefix is empty or only invalid chars.
    /// </summary>
    public static string? NormalizePrefix(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
            {
                sb.Append(ch);
            }
            else if (ch is >= 'A' and <= 'Z')
            {
                sb.Append((char)(ch + ('a' - 'A')));
            }
        }
        return sb.Length == 0 ? null : sb.ToString();
    }
}
