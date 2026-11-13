using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class FileAttachmentScopeTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "cab-attachment-scope-" + Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private readonly string _data;
    private readonly AttachmentFileCollector _collector = new();

    public FileAttachmentScopeTests()
    {
        _workspace = Path.Combine(_parent, "workspace");
        _data = Path.Combine(_parent, "app-data");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_data);
    }

    public void Dispose() { if (Directory.Exists(_parent)) Directory.Delete(_parent, recursive: true); }

    private BridgeSettings Settings(string scope) => new() { WorkspaceRoot = _workspace, FileAttachmentScope = scope };

    private string Write(string relative, string text = "content")
    {
        var path = Path.Combine(_workspace, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task Run_scope_ignores_named_files()
    {
        Write("chart.png");
        var before = await _collector.CaptureBeforeAsync(_workspace, _data, default);
        var result = await _collector.ResolveNamedAsync(before, ["chart.png"], Settings("run"), DateTime.UtcNow.AddHours(-1), default);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task Conversation_scope_accepts_only_files_created_since_the_conversation_began()
    {
        var old = Write("old-report.md");
        File.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-1));
        var start = DateTime.UtcNow.AddMinutes(-5);
        Write("chart.png");
        var before = await _collector.CaptureBeforeAsync(_workspace, _data, default);

        var result = await _collector.ResolveNamedAsync(before, ["chart.png", "old-report.md"], Settings("conversation"), start, default);

        Assert.Equal("chart.png", Assert.Single(result.Candidates).RelativePath);
        Assert.Contains("old-report.md", result.ExcludedFiles);
    }

    [Fact]
    public async Task Workspace_scope_accepts_existing_files_but_keeps_the_exclusion_rules()
    {
        Write("docs/plan.md");
        Write(".env", "SECRET=1");
        Write("settings.json", "{}");
        Write("node_modules/pkg/readme.md");
        Write(".git/config");
        Write("tool.ps1");
        var outside = Path.Combine(_parent, "outside.txt");
        File.WriteAllText(outside, "outside");
        var before = await _collector.CaptureBeforeAsync(_workspace, _data, default);

        var result = await _collector.ResolveNamedAsync(before,
            ["docs/plan.md", ".env", "settings.json", "node_modules/pkg/readme.md", ".git/config", "tool.ps1", "../outside.txt", outside, "missing.txt"],
            Settings("workspace"), null, default);

        Assert.Equal(Path.Combine("docs", "plan.md"), Assert.Single(result.Candidates).RelativePath);
        // "../outside.txt" and its absolute path are the same file, so it is reported once.
        Assert.Equal(7, result.ExcludedFiles.Count);
    }

    [Fact]
    public void Markers_are_extracted_and_removed_from_the_reply()
    {
        const string reply = "グラフを作りました。\nCAB-ATTACH: charts/sales.png\n`CAB-ATTACH: report.md`\n以上です。";

        Assert.Equal(["charts/sales.png", "report.md"], FileAttachmentMarkers.Extract(reply));
        Assert.Equal("グラフを作りました。\n以上です。", FileAttachmentMarkers.Strip(reply));
    }

    [Fact]
    public void Prompt_describes_markers_only_for_wider_scopes()
    {
        Assert.DoesNotContain("CAB-ATTACH", FileAttachmentAgentPrompt.Build("x", Settings("run"), true));
        Assert.Contains("CAB-ATTACH", FileAttachmentAgentPrompt.Build("x", Settings("conversation"), true));
        Assert.Contains("approves every such file", FileAttachmentAgentPrompt.Build("x", Settings("workspace"), true));
    }

    [Fact]
    public void Scope_is_part_of_the_consent_fingerprint_and_rejects_unknown_values()
    {
        var key = new SessionKey("mattermost", "c", "t", "u");
        Assert.NotEqual(FileAttachmentPolicy.GetConsentFingerprint(key, "s", Settings("run")),
            FileAttachmentPolicy.GetConsentFingerprint(key, "s", Settings("conversation")));
        Assert.Throws<ArgumentException>(() => FileAttachmentPolicy.NormalizeScope("everything"));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("first")]
    public async Task Workspace_scope_never_sends_without_confirmation(string mode)
    {
        Write("plan.md");
        var before = await _collector.CaptureBeforeAsync(_workspace, _data, default);
        var settings = Settings("workspace") with { FileAttachmentApprovalMode = mode };
        var named = await _collector.ResolveNamedAsync(before, ["plan.md"], settings, null, default);
        var uploaded = false;

        var outcome = await new FileAttachmentSharingService(_data).ShareAsync(
            new FileAttachmentShareContext(new SessionKey("mattermost", "c", "t", "u"), "session", settings, named.Candidates, true),
            automatic: true, confirm: null, validateScope: _ => Task.FromResult(true),
            upload: (_, _) => { uploaded = true; return Task.CompletedTask; });

        Assert.Equal(FileAttachmentShareOutcome.PendingConsent, outcome);
        Assert.False(uploaded);
    }

    [Fact]
    public async Task Conversation_start_is_remembered_per_conversation()
    {
        var store = new AttachmentConversationStore(_data);
        var first = await store.GetOrAddStartAsync(new SessionKey("mattermost", "c", "t1", "u"));
        await Task.Delay(20);
        Assert.Equal(first, await store.GetOrAddStartAsync(new SessionKey("mattermost", "c", "t1", "u")));
        Assert.NotEqual(first, await store.GetOrAddStartAsync(new SessionKey("mattermost", "c", "t2", "u")));
    }
}
