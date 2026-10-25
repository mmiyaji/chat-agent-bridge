using System.Text;

namespace ChatAgentBridge_Desktop.Search;

/// <summary>
/// Ranks a settings entry against a query without depending on settings state or UI types.
/// Lower values are better; entries that do not match receive -1.
/// </summary>
public static class SettingsSearchMatcher
{
    public static int Rank(string title, string location, IReadOnlyList<string> keywords, string query)
    {
        ArgumentNullException.ThrowIfNull(keywords);

        var normalizedQuery = Normalize(query);
        if (normalizedQuery.Length == 0)
        {
            return -1;
        }

        var terms = normalizedQuery.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
        {
            return -1;
        }

        var normalizedTitle = Normalize(title);
        var fields = new List<string>(keywords.Count + 2)
        {
            normalizedTitle,
            Normalize(location)
        };
        fields.AddRange(keywords.Select(Normalize));

        // Require every query term to occur somewhere in the entry. Terms can match
        // different fields, which lets a provider name in the location complete a match.
        if (terms.Any(term => !fields.Any(field => field.Contains(term, StringComparison.OrdinalIgnoreCase))))
        {
            return -1;
        }

        if (string.Equals(normalizedTitle, normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (normalizedTitle.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (normalizedTitle.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 3;
    }

    private static string Normalize(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        try
        {
            return trimmed.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            // Transient UI text can contain an unpaired UTF-16 surrogate. Keep that
            // text usable for ordinal matching instead of failing the search.
            return trimmed;
        }
    }
}
