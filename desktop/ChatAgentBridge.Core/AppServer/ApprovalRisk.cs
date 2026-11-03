using System.Text.RegularExpressions;

namespace ChatAgentBridge.Core.AppServer;

public enum ApprovalRiskLevel { Normal, Elevated, High }

/// <summary>Why an approval deserves a closer look. <see cref="ReasonKeys"/> are Core resource names.</summary>
public sealed record ApprovalRiskAssessment(ApprovalRiskLevel Level, IReadOnlyList<string> ReasonKeys)
{
    public static ApprovalRiskAssessment Normal { get; } = new(ApprovalRiskLevel.Normal, []);
}

/// <summary>
/// Heuristic, display-only classification of a command awaiting approval. It never grants or blocks anything by
/// itself: a miss only means the approver sees no warning, so patterns favour clear destructive or exfiltrating forms.
/// </summary>
public static class ApprovalRisk
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private const int MaximumInspected = 8192;

    private static Regex Rule(string pattern) => new(pattern, Options, MatchTimeout);

    private static readonly (string Key, ApprovalRiskLevel Level, Regex Pattern)[] Rules =
    [
        ("ApprovalRiskRecursiveDelete", ApprovalRiskLevel.High,
            Rule(@"\brm\s+(-[a-z]*r[a-z]*f|-[a-z]*f[a-z]*r|--recursive|-r\b)|\bRemove-Item\b[^|;]*-Recurse|\b(rd|rmdir)\s+/s\b|\bdel\s+(/[a-z]\s+)*/s\b|\bformat\s+[a-z]:")),
        ("ApprovalRiskForcePush", ApprovalRiskLevel.High,
            Rule(@"\bgit\s+push\b[^|;]*(--force\b|--force-with-lease|\s-f\b|\s\+\S)|\bgit\s+reset\s+--hard\b|\bgit\s+clean\s+-[a-z]*f")),
        ("ApprovalRiskPipeToShell", ApprovalRiskLevel.High,
            Rule(@"\b(curl|wget)\b[^|;]*\|\s*(sudo\s+)?(sh|bash|zsh|pwsh|powershell)\b|\b(iwr|irm|Invoke-WebRequest|Invoke-RestMethod)\b[^|;]*\|\s*(iex|Invoke-Expression)\b|\bInvoke-Expression\b")),
        ("ApprovalRiskElevation", ApprovalRiskLevel.High,
            Rule(@"(^|[\s;&|])sudo\s|\bRunAs\b|\bchmod\s+(-R\s+)?[0-7]*777\b|\bicacls\b[^|;]*/grant")),
        ("ApprovalRiskSystemChange", ApprovalRiskLevel.High,
            Rule(@"\b(reg\s+(add|delete)|Set-ItemProperty\s+[^|;]*HK(LM|CU)|New-ItemProperty\s+[^|;]*HK(LM|CU)|schtasks\s+/create|sc\s+(config|create|delete)|netsh\s|shutdown|diskpart)\b")),
        ("ApprovalRiskDatabaseDrop", ApprovalRiskLevel.High,
            Rule(@"\bdrop\s+(table|database|schema)\b|\btruncate\s+table\b|\bdelete\s+from\s+\w+\s*(;|$)")),
        ("ApprovalRiskNetworkTransfer", ApprovalRiskLevel.Elevated,
            Rule(@"\b(curl|wget|iwr|irm|Invoke-WebRequest|Invoke-RestMethod|scp|rsync|ftp|nc|ncat)\b")),
        ("ApprovalRiskPackageInstall", ApprovalRiskLevel.Elevated,
            Rule(@"\b(npm|pnpm|yarn)\s+(install|i|add)\b|\bpip3?\s+install\b|\bwinget\s+install\b|\bchoco\s+install\b|\bapt(-get)?\s+install\b|\bbrew\s+install\b|\bdotnet\s+tool\s+install\b|\bgem\s+install\b|\bcargo\s+install\b")),
        ("ApprovalRiskDelete", ApprovalRiskLevel.Elevated,
            Rule(@"(^|[\s;&|])(rm|del|erase|Remove-Item|rmdir|rd)\s")),
        ("ApprovalRiskGitWrite", ApprovalRiskLevel.Elevated,
            Rule(@"\bgit\s+(commit|merge|rebase|checkout|reset|clean|branch\s+-[dD]|tag\s+-d)\b")),
    ];

    public static ApprovalRiskAssessment Assess(AppServerApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        return Assess(approval.Command);
    }

    public static ApprovalRiskAssessment Assess(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return ApprovalRiskAssessment.Normal;
        var text = command.Length > MaximumInspected ? command[..MaximumInspected] : command;
        var level = ApprovalRiskLevel.Normal;
        var reasons = new List<string>();
        foreach (var (key, ruleLevel, pattern) in Rules)
        {
            bool hit;
            try { hit = pattern.IsMatch(text); }
            catch (RegexMatchTimeoutException) { continue; }
            if (!hit) continue;
            reasons.Add(key);
            if (ruleLevel > level) level = ruleLevel;
        }
        // A truncated command may hide anything past the cut, so the approver should know it was not fully inspected.
        if (command.Length > MaximumInspected)
        {
            reasons.Add("ApprovalRiskTruncated");
            if (level < ApprovalRiskLevel.Elevated) level = ApprovalRiskLevel.Elevated;
        }
        return reasons.Count == 0 ? ApprovalRiskAssessment.Normal : new(level, reasons);
    }

    /// <summary>One localized line: the level headline followed by the reasons.</summary>
    public static string Describe(ApprovalRiskAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        if (assessment.Level == ApprovalRiskLevel.Normal) return "";
        var headline = Localization.CoreText.Get(assessment.Level == ApprovalRiskLevel.High ? "ApprovalRiskHigh" : "ApprovalRiskElevated");
        return headline + " " + string.Join(" / ", assessment.ReasonKeys.Select(key => Localization.CoreText.Get(key)));
    }
}
