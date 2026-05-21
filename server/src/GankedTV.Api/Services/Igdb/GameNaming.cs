using System.Globalization;
using System.Text;

namespace GankedTV.Api.Services.Igdb;

/// <summary>
/// Derives a URL slug and a short display tag from an IGDB game name. Pure (no DB);
/// the import command resolves slug collisions deterministically against existing rows.
/// </summary>
public static class GameNaming
{
    // games.slug column is varchar(255); games.tag is varchar(16).
    public const int MaxSlugLength = 255;
    public const int MaxTagLength = 16;
    private const string Fallback = "game";

    // Connector words dropped from multi-word acronyms ("League of Legends" → "LL", not "LOL"
    // — the curated seeds keep their hand-picked tags; this is only for auto-imported titles).
    private static readonly HashSet<string> Stopwords =
        new(StringComparer.Ordinal) { "of", "the", "and", "a", "an" };

    /// <summary>
    /// Lowercase, ASCII-folded, hyphen-separated slug. "Tom Clancy's Rainbow Six® Siege"
    /// → "tom-clancys-rainbow-six-siege".
    /// </summary>
    public static string Slug(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Fallback;
        }

        var decomposed = name.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var pendingSeparator = false;

        foreach (var ch in decomposed.ToLowerInvariant())
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (pendingSeparator && sb.Length > 0)
                {
                    sb.Append('-');
                }
                pendingSeparator = false;
                sb.Append(ch);
            }
            else if (ch is '\'' or '’' or '`')
            {
                // Drop apostrophes within a word: "Clancy's" → "clancys", not "clancy-s".
            }
            else
            {
                // Any other char (space, punctuation, symbol) becomes a single separator,
                // collapsing runs so "rainbow  six" / "six®" don't yield double hyphens.
                pendingSeparator = true;
            }
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length == 0)
        {
            return Fallback;
        }

        return slug.Length <= MaxSlugLength ? slug : slug[..MaxSlugLength].Trim('-');
    }

    /// <summary>
    /// Short uppercase tag (≤16 chars). Multi-word names become an acronym
    /// ("League of Legends" → "LOL"); single-word names are uppercased and truncated.
    /// </summary>
    public static string Tag(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Fallback.ToUpperInvariant();
        }

        var words = Slug(name).Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return Fallback.ToUpperInvariant();
        }

        string tag;
        if (words.Length == 1)
        {
            tag = words[0].ToUpperInvariant();
        }
        else
        {
            // Acronym from each word's first character; ignore very short connector words
            // (of, the, &) unless that would leave nothing.
            var initials = new StringBuilder(words.Length);
            foreach (var w in words)
            {
                if (Stopwords.Contains(w) && initials.Length > 0)
                {
                    continue;
                }
                initials.Append(char.ToUpperInvariant(w[0]));
            }
            tag = initials.Length > 0 ? initials.ToString() : words[0].ToUpperInvariant();
        }

        return tag.Length <= MaxTagLength ? tag : tag[..MaxTagLength];
    }
}
