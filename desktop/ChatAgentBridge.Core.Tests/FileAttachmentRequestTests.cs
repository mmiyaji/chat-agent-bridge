namespace ChatAgentBridge.Core.Tests;

public sealed class FileAttachmentRequestTests
{
    [Theory]
    [InlineData("さっきの画像を添付して")]
    [InlineData("画像を送って")]
    [InlineData("そのファイルをください")]
    [InlineData("Please attach the chart")]
    [InlineData("send me the screenshot")]
    public void Detects_attachment_requests(string text) => Assert.True(FileAttachmentRequest.IsRequested(text));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("テストを実行して結果を教えて")]
    public void Ignores_other_requests(string? text) => Assert.False(FileAttachmentRequest.IsRequested(text));
}
