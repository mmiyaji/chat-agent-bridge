namespace ChatAgentBridge_Desktop.Inbox;

internal static class InboxListPrompt
{
    // Matches Replace(CR/LF, ' ').Trim()[..120] without allocating a normalized
    // copy of potentially very large prompts for every list-row binding.
    internal static string Get(string prompt)
    {
        var start = 0;
        var end = prompt.Length;
        while (start < end && char.IsWhiteSpace(prompt[start])) start++;
        while (end > start && char.IsWhiteSpace(prompt[end - 1])) end--;
        var length = Math.Min(end - start, 120);
        if (length == 0) return "";
        var chars = prompt.AsSpan(start, length).ToArray();
        for (var index = 0; index < chars.Length; index++)
            if (chars[index] is '\r' or '\n') chars[index] = ' ';
        return new string(chars);
    }
}
