namespace ChatAgentBridge.Core;

/// <summary>Bounds the public chat status response to a small number of rows and characters.</summary>
public static class ChatControlStatusFormatter
{
    public const int MaximumRows = 10;
    public const int MaximumCharacters = 3800;

    public static string Format(string header, IReadOnlyList<string> rows, int totalRows, Func<int, string> moreLabel,
        int maxCharacters = MaximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(moreLabel);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(totalRows, rows.Count);

        var lines = new List<string>(Math.Min(rows.Count, MaximumRows) + 2) { header };
        var shown = Math.Min(rows.Count, MaximumRows);
        for (var index = 0; index < shown; index++) lines.Add(rows[index]);
        if (totalRows > shown) lines.Add(moreLabel(totalRows - shown));

        var response = string.Join(Environment.NewLine, lines);
        if (response.Length <= maxCharacters) return response;
        var keep = maxCharacters - 1;
        if (keep > 0 && char.IsHighSurrogate(response[keep - 1])) keep--;
        return response[..keep] + "…";
    }
}
