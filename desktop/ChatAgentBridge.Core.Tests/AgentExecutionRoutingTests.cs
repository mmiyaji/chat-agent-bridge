using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class AgentExecutionRoutingTests
{
    [Theory]
    [InlineData(AgentProfile.Codex, AgentProfile.ExecutionKind.Process)]
    [InlineData(AgentProfile.Claude, AgentProfile.ExecutionKind.Process)]
    [InlineData(AgentProfile.Gemini, AgentProfile.ExecutionKind.Process)]
    [InlineData(AgentProfile.GitHubCopilot, AgentProfile.ExecutionKind.Process)]
    [InlineData(AgentProfile.Antigravity, AgentProfile.ExecutionKind.Process)]
    [InlineData(AgentProfile.Kiro, AgentProfile.ExecutionKind.Process)]
    [InlineData(AgentProfile.Qwen, AgentProfile.ExecutionKind.Process)]
    [InlineData(AgentProfile.Kimi, AgentProfile.ExecutionKind.KimiAcp)]
    [InlineData(AgentProfile.Muse, AgentProfile.ExecutionKind.MuseExec)]
    [InlineData(AgentProfile.Custom, AgentProfile.ExecutionKind.Process)]
    public void ProviderSelectsExpectedExecutionAdapter(string provider, AgentProfile.ExecutionKind expected)
        => Assert.Equal(expected, AgentProfile.GetExecutionKind(provider));

    [Fact]
    public async Task PreCancelledRunStillCancelsBeforeWorkspaceOrAdapterWork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AgentRunner().RunAsync(
            new BridgeSettings { AgentProvider = AgentProfile.Kimi }, "prompt", cancellation.Token));
    }
}
