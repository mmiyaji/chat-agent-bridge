namespace ChatAgentBridge.Core;

public static class FileAttachmentRequest
{
    // Matches requests such as "画像を添付して" or "attach the chart", not "添付したファイルを読んで", "添付してある資料を読んで" or "read the attached files"; used only to decide whether "no new file" is worth a reply.
    private static readonly System.Text.RegularExpressions.Regex Pattern = new(
        @"添付し(?!てある|ている|てあった|てた)(て|てください|てほしい|てもらえ)|添付で(送|返|ちょうだい|ください)|ファイル(を|で)?(送|ちょうだい|ください|下さい|頂戴)|画像(を|で)?(送|貼|ちょうだい|ください|下さい|頂戴)|\battach(?!ed|ment)|\bupload(?!ed)|send (me )?(the |a |an )?(file|image|picture|screenshot)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool IsRequested(string? text) => !string.IsNullOrEmpty(text) && Pattern.IsMatch(text);
}
