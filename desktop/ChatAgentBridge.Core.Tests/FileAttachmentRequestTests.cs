namespace ChatAgentBridge.Core.Tests;

public sealed class FileAttachmentRequestTests
{
    [Theory]
    [InlineData("さっきの画像を添付して")]
    [InlineData("グラフにして添付してください")]
    [InlineData("結果を添付で返して")]
    [InlineData("画像を送って")]
    [InlineData("そのファイルをください")]
    [InlineData("Please attach the chart")]
    [InlineData("send me the screenshot")]
    [InlineData("attach it here")]
    [InlineData("upload the report")]
    public void Detects_attachment_requests(string text) => Assert.True(FileAttachmentRequest.IsRequested(text));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("テストを実行して結果を教えて")]
    [InlineData("添付した3つのファイルを読んで要約して")]
    [InlineData("添付ファイルの中身を確認して")]
    [InlineData("添付してある資料を読んで")]
    [InlineData("添付している画像を見て")]
    [InlineData("Read the attached files")]
    [InlineData("Check the attachment contents")]
    [InlineData("summarize the uploaded file")]
    public void Ignores_other_requests(string? text) => Assert.False(FileAttachmentRequest.IsRequested(text));
}
