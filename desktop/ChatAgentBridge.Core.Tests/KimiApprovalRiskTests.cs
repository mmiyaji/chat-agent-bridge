using ChatAgentBridge.Core.AppServer;

namespace ChatAgentBridge.Core.Tests;

public sealed class KimiApprovalRiskTests
{
    [Fact]
    public void OpaqueKimiPermissionRequestIsElevated()
    {
        var assessment = ApprovalRisk.Assess(new AppServerApproval(
            "session/request_permission", "session", "", "tool", "Tool", null, null));

        Assert.Equal(ApprovalRiskLevel.Elevated, assessment.Level);
        Assert.Contains("ApprovalRiskUnknownTool", assessment.ReasonKeys);
    }

    [Fact]
    public void KimiCommandIsClassifiedFromActualShellText()
    {
        var assessment = ApprovalRisk.Assess(new AppServerApproval(
            "session/request_permission", "session", "", "tool", "Shell", "rm -rf /", null));

        Assert.Equal(ApprovalRiskLevel.High, assessment.Level);
        Assert.Contains("ApprovalRiskRecursiveDelete", assessment.ReasonKeys);
    }
}
