using System.Text.RegularExpressions;

namespace ChatAgentBridge.Core;

/// <summary>
/// Lines such as "CAB-ATTACH: reports/summary.pdf" in an agent's final reply name existing files to attach.
/// They are requests only: the configured scope, exclusion rules and approval still decide what is sent.
/// </summary>
public static partial class FileAttachmentMarkers
{
    public const int MaxNames = 20;

    [GeneratedRegex(@"^[ \t]*`?CAB-ATTACH:[ \t]*(?<path>[^`\r\n]+?)[ \t]*`?[ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex MarkerLine();

    [GeneratedRegex(@"^[ \t]*`?CAB-ATTACH:[^\r\n]*(\r?\n)?", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex MarkerLineWithBreak();

    public static IReadOnlyList<string> Extract(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        return MarkerLine().Matches(text).Select(match => match.Groups["path"].Value.Trim().Trim('"', '\''))
            .Where(path => path.Length > 0).Distinct(StringComparer.Ordinal).Take(MaxNames).ToList();
    }

    /// <summary>Removes marker lines so they do not appear in the chat reply.</summary>
    public static string Strip(string text) =>
        string.IsNullOrEmpty(text) ? text : MarkerLineWithBreak().Replace(text, "").TrimEnd();
}
