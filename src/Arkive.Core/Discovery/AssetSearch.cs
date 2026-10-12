namespace Arkive.Core.Discovery;

public static class AssetSearch
{
    /// <summary>
    /// Matches every whitespace-separated term against any supplied field.
    /// Prefix a term with '-' to exclude it.
    /// </summary>
    public static bool Matches(string query, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        foreach (var rawTerm in query.Split(
                     (char[]?)null,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var excluded = rawTerm.Length > 1 && rawTerm[0] == '-';
            var term = excluded ? rawTerm[1..] : rawTerm;
            var contains = fields.Any(field =>
                !string.IsNullOrEmpty(field) &&
                field.Contains(term, StringComparison.OrdinalIgnoreCase));
            if ((!excluded && !contains) || (excluded && contains))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Matches display-name terms at a token boundary while retaining prefix
    /// searches (for example, "Kazer" matches "Kazeros"). This prevents a set
    /// query such as "Romantic Wear" from treating the suffix of "Facewear" as
    /// the separate word "Wear".
    /// </summary>
    public static bool MatchesDisplayNames(string query, IEnumerable<string?> fields)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var values = fields.Where(field => !string.IsNullOrEmpty(field)).ToArray();
        foreach (var rawTerm in query.Split(
                     (char[]?)null,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var excluded = rawTerm.Length > 1 && rawTerm[0] == '-';
            var term = excluded ? rawTerm[1..] : rawTerm;
            var contains = values.Any(field => ContainsAtTokenStart(field!, term));
            if ((!excluded && !contains) || (excluded && contains))
            {
                return false;
            }
        }

        return true;
    }

    public static bool ContainsAtTokenStart(string value, string term)
    {
        if (string.IsNullOrEmpty(term))
        {
            return true;
        }

        var offset = 0;
        while (offset <= value.Length - term.Length)
        {
            var index = value.IndexOf(term, offset, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            if (index == 0 || !char.IsLetterOrDigit(value[index - 1]))
            {
                return true;
            }

            offset = index + 1;
        }

        return false;
    }
}
