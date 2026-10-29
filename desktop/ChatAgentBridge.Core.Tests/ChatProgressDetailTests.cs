using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class ChatProgressDetailTests
{
    [Theory]
    [InlineData("minimal", "minimal")]
    [InlineData("standard", "standard")]
    [InlineData("detailed", "detailed")]
    [InlineData(null, "standard")]
    [InlineData("", "standard")]
    [InlineData("verbose", "standard")]
    public void NormalizeFallsBackToStandard(string? value, string expected) =>
        Assert.Equal(expected, ChatProgressDetail.Normalize(value));

    [Fact]
    public void DefaultIsStandard() =>
        Assert.Equal(ChatProgressDetail.Standard, new BridgeSettings().ChatProgressDetail);
}
