using System.Globalization;
using System.Text.RegularExpressions;

namespace ChatAgentBridge.Core;

public enum ChatControlCommandKind
{
    None,
    Help,
    Status,
    Stop,
    Model,
    Answer,
    ReservedInvalid
}

/// <summary>A parsed, reserved command from a chat message. Parsing does not grant authorization.</summary>
public sealed record ChatControlCommand(
    ChatControlCommandKind Kind,
    string? Code = null,
    int? QuestionNumber = null,
    string? Answer = null,
    string? ModelArgument = null)
{
    private static readonly Regex AnswerPattern = new(
        @"^answer\s+(?<code>[0-9a-f]{12})\s+(?<number>[1-9][0-9]{0,2})\s+(?<answer>\S[\s\S]*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ChatControlCommand Parse(string? text)
    {
        var value = text?.Trim() ?? string.Empty;
        if (value.Length == 0) return new(ChatControlCommandKind.None);

        if (value.StartsWith("!cab", StringComparison.OrdinalIgnoreCase) &&
            (value.Length == 4 || char.IsWhiteSpace(value[4])))
        {
            value = value.Length == 4 ? string.Empty : value[4..].TrimStart();
        }

        if (value.Length == 0) return new(ChatControlCommandKind.None);
        int commandEnd = value.IndexOfAny([' ', '\t', '\r', '\n']);
        var command = commandEnd < 0 ? value : value[..commandEnd];
        if (command.Equals("model", StringComparison.OrdinalIgnoreCase))
            return new(ChatControlCommandKind.Model, ModelArgument: commandEnd < 0 ? "" : value[commandEnd..].Trim());
        if ((command.Equals("help", StringComparison.OrdinalIgnoreCase) ||
             command.Equals("command", StringComparison.OrdinalIgnoreCase) ||
             command.Equals("commands", StringComparison.OrdinalIgnoreCase)) && commandEnd < 0)
            return new(ChatControlCommandKind.Help);
        if (command.Equals("status", StringComparison.OrdinalIgnoreCase) && commandEnd < 0)
            return new(ChatControlCommandKind.Status);
        if (command.Equals("stop", StringComparison.OrdinalIgnoreCase) && commandEnd < 0)
            return new(ChatControlCommandKind.Stop);
        if (!command.Equals("answer", StringComparison.OrdinalIgnoreCase))
            return new(ChatControlCommandKind.None);

        Match match = AnswerPattern.Match(value);
        if (!match.Success ||
            !int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ||
            number is < 1 or > 100)
            return new(ChatControlCommandKind.ReservedInvalid);

        var answer = match.Groups["answer"].Value.Trim();
        if (answer.Length == 0 || answer.Length > 2000)
            return new(ChatControlCommandKind.ReservedInvalid);
        return new(ChatControlCommandKind.Answer, match.Groups["code"].Value.ToUpperInvariant(), number, answer);
    }
}
