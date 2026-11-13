namespace ChatAgentBridge.Core;

public static class FileAttachmentRequest
{
    // Matches requests such as "画像を添付して" or "attach the chart"; used only to decide whether "no new file" is worth a reply.
    private static readonly System.Text.RegularExpressions.Regex Pattern = new(
        @"添付|ファイル(を|で)?(送|ちょうだい|ください|下さい|頂戴)|画像(を|で)?(送|貼|ちょうだい|ください|下さい|頂戴)|\battach|\bupload|send (me )?(the |a |an )?(file|image|picture|screenshot)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool IsRequested(string? text) => !string.IsNullOrEmpty(text) && Pattern.IsMatch(text);
}
