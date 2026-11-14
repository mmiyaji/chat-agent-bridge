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
        var result = await _collector.ResolveNamedAsync(before, ["chart.png"], Settings("run"), null, default);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task Conversation_scope_rejects_files_without_a_discovery_record()
    {
        var old = Write("old-report.md");
        File.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-1));
        Write("chart.png");
        var before = await _collector.CaptureBeforeAsync(_workspace, _data, default);

        var result = await _collector.ResolveNamedAsync(before, ["chart.png", "old-report.md"], Settings("conversation"), [], default);

        Assert.Empty(result.Candidates);
        Assert.Contains("chart.png", result.ExcludedFiles);
        Assert.Contains("old-report.md", result.ExcludedFiles);
    }

    [Fact]
    public async Task Conversation_scope_allows_an_earlier_output_after_store_reload()
    {
        var key = new SessionKey("mattermost", "channel", "thread", "user", "origin-a");
        var store = new AttachmentConversationStore(_data);
        await DiscoverAndRecordAsync(store, key, _workspace, "earlier-output.md", "earlier output");

        var reloadedStore = new AttachmentConversationStore(_data);
        var eligible = await reloadedStore.GetDiscoveredAsync(key, _workspace + Path.DirectorySeparatorChar);
        var before = await _collector.CaptureBeforeAsync(_workspace, _data, default);
        var result = await _collector.ResolveNamedAsync(before, ["earlier-output.md"], Settings("conversation"), eligible, default);
        var uploaded = false;
        var outcome = await new FileAttachmentSharingService(_data).ShareAsync(
            new FileAttachmentShareContext(key, "session", Settings("conversation") with { FileAttachmentApprovalMode = "auto" }, result.Candidates, true),
            automatic: true, confirm: null, validateScope: _ => Task.FromResult(true),
            upload: (_, _) => { uploaded = true; return Task.CompletedTask; });

        Assert.Equal("earlier-output.md", Assert.Single(result.Candidates).RelativePath);
        Assert.Equal(FileAttachmentShareOutcome.Sent, outcome);
        Assert.True(uploaded);
    }

    [Fact]
    public async Task Conversation_scope_does_not_share_another_users_or_threads_discovered_file()
    {
        var owner = new SessionKey("mattermost", "channel", "thread", "user-a", "origin-a");
        var store = new AttachmentConversationStore(_data);
        await DiscoverAndRecordAsync(store, owner, _workspace, "private-output.md", "private output");
        var before = await _collector.CaptureBeforeAsync(_workspace, _data, default);
        var settings = Settings("conversation") with { FileAttachmentApprovalMode = "auto" };
        var foreignKeys = new[]
        {
            new SessionKey("mattermost", "channel", "thread", "user-b", "origin-a"), // same thread, different user
            new SessionKey("mattermost", "channel", "other-thread", "user-a", "origin-a"), // same user, different thread
            new SessionKey("mattermost", "channel", "thread", "user-a", "origin-b") // different origin
        };

        foreach (var foreign in foreignKeys)
        {
            var eligible = await store.GetDiscoveredAsync(foreign, _workspace);
            var named = await _collector.ResolveNamedAsync(before, ["private-output.md"], settings, eligible, default);
            var uploaded = false;
            var outcome = await new FileAttachmentSharingService(_data).ShareAsync(
                new FileAttachmentShareContext(foreign, "session", settings, named.Candidates, true), automatic: true,
                confirm: null, validateScope: _ => Task.FromResult(true),
                upload: (_, _) => { uploaded = true; return Task.CompletedTask; });
            Assert.Empty(named.Candidates);
            Assert.Equal(FileAttachmentShareOutcome.None, outcome);
            Assert.False(uploaded);
        }
    }

    [Fact]
    public async Task Conversation_scope_records_are_bound_to_the_workspace()
    {
        var otherWorkspace = Path.Combine(_parent, "other-workspace");
        Directory.CreateDirectory(otherWorkspace);
        var key = new SessionKey("mattermost", "channel", "thread", "user", "origin");
        var store = new AttachmentConversationStore(_data);
        await DiscoverAndRecordAsync(store, key, _workspace, "report.md", "report");
        File.WriteAllText(Path.Combine(otherWorkspace, "report.md"), "report");
        var otherSettings = Settings("conversation") with { WorkspaceRoot = otherWorkspace };
        var otherBefore = await _collector.CaptureBeforeAsync(otherWorkspace, _data, default);
        var eligible = await store.GetDiscoveredAsync(key, otherWorkspace);
        var result = await _collector.ResolveNamedAsync(otherBefore, ["report.md"], otherSettings, eligible, default);

        Assert.Empty(eligible);
        Assert.Empty(result.Candidates);
        Assert.Contains("report.md", result.ExcludedFiles);
    }

    [Fact]
    public async Task Conversation_scope_rejects_a_file_changed_after_registration()
    {
        var key = new SessionKey("mattermost", "channel", "thread", "user", "origin");
        var store = new AttachmentConversationStore(_data);
        var path = await DiscoverAndRecordAsync(store, key, _workspace, "replace-me.md", "original");
        var registeredTime = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "tampered");
        File.SetLastWriteTimeUtc(path, registeredTime);
        var before = await _collector.CaptureBeforeAsync(_workspace, _data, default);
        var eligible = await store.GetDiscoveredAsync(key, _workspace);
        var result = await _collector.ResolveNamedAsync(before, ["replace-me.md"], Settings("conversation"), eligible, default);

        Assert.Empty(result.Candidates);
        Assert.Contains("replace-me.md", result.ExcludedFiles);
    }

    [Fact]
    public async Task Incomplete_discovery_does_not_add_conversation_eligibility()
    {
        var key = new SessionKey("mattermost", "channel", "thread", "user", "origin");
        var store = new AttachmentConversationStore(_data);
        var before = await _collector.CaptureBeforeAsync(_workspace, _data, default);
        var path = Write("uncertain.md", "uncertain");
        var discovered = await _collector.DiscoverAsync(before, Settings("conversation"), default);

        await store.RecordDiscoveredAsync(key, _workspace, discovered.Candidates, scanComplete: false);

        Assert.Empty(await store.GetDiscoveredAsync(key, _workspace));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Legacy_timestamp_only_data_does_not_grant_conversation_file_access()
    {
        Write("old-data.md");
        var legacy = "[{\"Provider\":\"mattermost\",\"ChannelId\":\"channel\",\"ThreadId\":\"thread\",\"UserId\":\"user\",\"Origin\":\"origin\",\"StartUtc\":\"2026-01-01T00:00:00Z\"}]";
        await File.WriteAllTextAsync(Path.Combine(_data, "attachment-conversations.json"), legacy);
        var store = new AttachmentConversationStore(_data);
        var eligible = await store.GetDiscoveredAsync(new SessionKey("mattermost", "channel", "thread", "user", "origin"), _workspace);
        var before = await _collector.CaptureBeforeAsync(_workspace, _data, default);
        var result = await _collector.ResolveNamedAsync(before, ["old-data.md"], Settings("conversation"), eligible, default);

        Assert.Empty(result.Candidates);
        Assert.Contains("old-data.md", result.ExcludedFiles);
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

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Markers_are_extracted_and_removed_from_lf_and_crlf_replies(string newline)
    {
        var reply = $"グラフを作りました。{newline}CAB-ATTACH: charts/sales.png{newline}説明文{newline}`CAB-ATTACH: report.md`{newline}以上です。";

        Assert.Equal(["charts/sales.png", "report.md"], FileAttachmentMarkers.Extract(reply));
        Assert.Equal($"グラフを作りました。{newline}説明文{newline}以上です。", FileAttachmentMarkers.Strip(reply));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Marker_at_eof_without_final_newline_is_extracted_and_removed(string newline)
    {
        var reply = $"結果です。{newline}`CAB-ATTACH: \"reports/final.md\"`";

        Assert.Equal(["reports/final.md"], FileAttachmentMarkers.Extract(reply));
        Assert.Equal("結果です。", FileAttachmentMarkers.Strip(reply));
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
    public async Task Conversation_records_are_separate_per_user_and_thread()
    {
        var store = new AttachmentConversationStore(_data);
        var first = new SessionKey("mattermost", "c", "t1", "u");
        await DiscoverAndRecordAsync(store, first, _workspace, "user-a-thread-1.md", "one");
        await DiscoverAndRecordAsync(store, new SessionKey("mattermost", "c", "t1", "other-user"), _workspace, "user-b-thread-1.md", "two");
        await DiscoverAndRecordAsync(store, new SessionKey("mattermost", "c", "t2", "u"), _workspace, "user-a-thread-2.md", "three");

        Assert.Equal("user-a-thread-1.md", Assert.Single(await store.GetDiscoveredAsync(first, _workspace)).RelativePath);
        Assert.Equal("user-b-thread-1.md", Assert.Single(await store.GetDiscoveredAsync(new SessionKey("mattermost", "c", "t1", "other-user"), _workspace)).RelativePath);
        Assert.Equal("user-a-thread-2.md", Assert.Single(await store.GetDiscoveredAsync(new SessionKey("mattermost", "c", "t2", "u"), _workspace)).RelativePath);
    }

    private async Task<string> DiscoverAndRecordAsync(AttachmentConversationStore store, SessionKey key, string workspace,
        string relativePath, string contents)
    {
        var before = await _collector.CaptureBeforeAsync(workspace, _data, default);
        var path = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);
        var discovered = await _collector.DiscoverAsync(before, Settings("conversation") with { WorkspaceRoot = workspace }, default);
        Assert.True(discovered.IsComplete);
        Assert.Single(discovered.Candidates);
        await store.RecordDiscoveredAsync(key, workspace, discovered.Candidates, discovered.IsComplete);
        return path;
    }
}
