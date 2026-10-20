using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class FileAttachmentSharingServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cab-attachment-consent-" + Guid.NewGuid().ToString("N"));
    private readonly SessionKey _key = new("mattermost", "channel", "thread", "owner", "server");
    private string Workspace => Path.Combine(_root, "work");
    private string Data => Path.Combine(_root, "private");
    private int _uploads;
    private int _confirmations;

    private async Task<FileAttachmentShareContext> ContextAsync(string mode = "always", string session = "native-session")
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Data);
        var settings = new BridgeSettings { WorkspaceRoot = Workspace, TrustedWorkspaceRoots = Workspace,
            AgentProvider = "claude", AgentExecutable = "claude", AgentArguments = "", AgentPermissionMode = "read-only",
            ChatResultMode = "auto", FileAttachmentApprovalMode = mode };
        var collector = new AttachmentFileCollector();
        var before = await collector.CaptureBeforeAsync(Workspace, Data, default);
        await File.WriteAllTextAsync(Path.Combine(Workspace, Guid.NewGuid().ToString("N") + ".txt"), "Synthetic non-sensitive output");
        var after = await collector.DiscoverAsync(before, settings, default);
        return new(_key, session, settings, after.Candidates, after.IsComplete);
    }

    private Task Upload(IReadOnlyList<PreparedFileAttachment> files, CancellationToken token)
    { token.ThrowIfCancellationRequested(); Assert.NotEmpty(files); _uploads++; return Task.CompletedTask; }
    private Task<IReadOnlyList<FileAttachmentCandidate>?> Accept(IReadOnlyList<FileAttachmentCandidate> files, bool first, CancellationToken token)
    { _confirmations++; return Task.FromResult<IReadOnlyList<FileAttachmentCandidate>?>(files); }
    private static Task<bool> Allowed(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(true); }

    [Fact]
    public async Task TextAutoReplyAndExecutionApprovalNeverAuthorizeDefaultFileUpload()
    {
        var context = await ContextAsync();
        Assert.Equal("always", new BridgeSettings().FileAttachmentApprovalMode);
        var outcome = await new FileAttachmentSharingService(Data).ShareAsync(context, true, Accept, Allowed, Upload);
        Assert.Equal(FileAttachmentShareOutcome.PendingConsent, outcome);
        Assert.Equal(0, _uploads); Assert.Equal(0, _confirmations);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("always")]
    [InlineData("first")]
    public async Task NoAutomaticUploadWithoutTheSpecificFileAuthorization(string mode)
    {
        var result = await new FileAttachmentSharingService(Data).ShareAsync(await ContextAsync(mode), true, Accept, Allowed, Upload);
        Assert.NotEqual(FileAttachmentShareOutcome.Sent, result); Assert.Equal(0, _uploads);
    }

    [Fact]
    public async Task ExplicitSelectionUploadsExactlyThoseFiles()
    {
        var context = await ContextAsync();
        Assert.Equal(FileAttachmentShareOutcome.Sent,
            await new FileAttachmentSharingService(Data).ShareAsync(context, false, Accept, Allowed, Upload));
        Assert.Equal(1, _uploads); Assert.Equal(1, _confirmations);
    }

    [Fact]
    public async Task RejectingOrLackingTheDialogNeverUploads()
    {
        var context = await ContextAsync();
        var service = new FileAttachmentSharingService(Data);
        Assert.Equal(FileAttachmentShareOutcome.PendingConsent, await service.ShareAsync(context, false, null, Allowed, Upload));
        Assert.Equal(FileAttachmentShareOutcome.Cancelled, await service.ShareAsync(context, false,
            (_, _, _) => Task.FromResult<IReadOnlyList<FileAttachmentCandidate>?>(null), Allowed, Upload));
        Assert.Equal(0, _uploads);
    }

    [Fact]
    public async Task FirstConsentAppliesOnlyToTheExactUserSessionAndPolicy()
    {
        var context = await ContextAsync("first");
        var service = new FileAttachmentSharingService(Data);
        Assert.Equal(FileAttachmentShareOutcome.Sent, await service.ShareAsync(context, false, Accept, Allowed, Upload));
        Assert.Equal(FileAttachmentShareOutcome.Sent, await service.ShareAsync(context, true, Accept, Allowed, Upload));
        foreach (var changed in new[]
        {
            context with { Key = _key with { UserId = "another" } },
            context with { Key = _key with { ThreadId = "another" } },
            context with { Key = _key with { Origin = "another" } },
            context with { SessionId = "another" },
            context with { Settings = context.Settings with { FileAttachmentMaxCount = 4 } },
            context with { Settings = context.Settings with { AgentExecutable = "another-cli" } }
        })
            Assert.Equal(FileAttachmentShareOutcome.PendingConsent, await service.ShareAsync(changed, true, Accept, Allowed, Upload));
        Assert.Equal(2, _uploads); Assert.Equal(1, _confirmations);
    }

    [Fact]
    public async Task RevocationWhileTheUserConfirmsStopsAllExternalTraffic()
    {
        var context = await ContextAsync(); var allowed = true;
        var result = await new FileAttachmentSharingService(Data).ShareAsync(context, false,
            (files, _, _) => { allowed = false; return Task.FromResult<IReadOnlyList<FileAttachmentCandidate>?>(files); },
            _ => Task.FromResult(allowed), Upload);
        Assert.Equal(FileAttachmentShareOutcome.ScopeChanged, result); Assert.Equal(0, _uploads);
    }

    [Fact]
    public async Task FileMutationDuringConsentIsNotUploaded()
    {
        var context = await ContextAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => new FileAttachmentSharingService(Data).ShareAsync(context, false,
            async (files, _, _) => { await File.WriteAllTextAsync(files[0].FullPath, "Changed after selection"); return files; }, Allowed, Upload));
        Assert.Equal(0, _uploads);
    }

    [Theory]
    [InlineData(false, "auto")]
    [InlineData(false, "first")]
    public async Task AnIncompleteScanCannotAuthorizeAutomaticSharing(bool complete, string mode)
    {
        var context = await ContextAsync(mode);
        if (mode == "first")
            await new AttachmentConsentStore(Data).SaveAsync(_key, context.SessionId,
                FileAttachmentPolicy.GetConsentFingerprint(_key, context.SessionId, context.Settings));
        Assert.Equal(FileAttachmentShareOutcome.PendingConsent,
            await new FileAttachmentSharingService(Data).ShareAsync(context with { ScanComplete = complete }, true, Accept, Allowed, Upload));
        Assert.Equal(0, _uploads);
    }

    [Fact]
    public async Task AutomaticModeIsAnExplicitIndependentOptIn()
    {
        var context = await ContextAsync("auto");
        Assert.Equal(FileAttachmentShareOutcome.Sent,
            await new FileAttachmentSharingService(Data).ShareAsync(context, true, null, Allowed, Upload));
        Assert.Equal(1, _uploads); Assert.Equal(0, _confirmations);
    }

    [Fact]
    public async Task FailedUploadIsNeverRetriedAndDoesNotGrantFirstConsent()
    {
        var context = await ContextAsync("first"); var attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => new FileAttachmentSharingService(Data).ShareAsync(context, false, Accept, Allowed,
            (_, _) => { attempts++; throw new IOException("Synthetic upload failure"); }));
        Assert.Equal(1, attempts);
        Assert.False(await new AttachmentConsentStore(Data).IsApprovedAsync(_key, context.SessionId,
            FileAttachmentPolicy.GetConsentFingerprint(_key, context.SessionId, context.Settings)));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("each")]
    [InlineData("total")]
    public async Task AutomaticSharingNeverSilentlyChoosesASubsetWhenLimitsAreExceeded(string limit)
    {
        var context = await ContextAsync("auto");
        var file = Assert.Single(context.Candidates);
        context = limit switch
        {
            "count" => context with { Settings = context.Settings with { FileAttachmentMaxCount = 1 }, Candidates = [file, file] },
            "each" => context with { Settings = context.Settings with { FileAttachmentMaxSizeMb = 1 }, Candidates = [file with { Length = 2 * 1024 * 1024 }] },
            _ => context with { Settings = context.Settings with { FileAttachmentMaxTotalSizeMb = 1 }, Candidates = [file with { Length = 2 * 1024 * 1024 }] }
        };
        Assert.Equal(FileAttachmentShareOutcome.PendingConsent,
            await new FileAttachmentSharingService(Data).ShareAsync(context, true, Accept, Allowed, Upload));
        Assert.Equal(0, _uploads); Assert.Equal(0, _confirmations);
    }

    [Fact]
    public async Task PrivateResultPolicyAlsoBlocksFileSharing()
    {
        var context = await ContextAsync("auto");
        Assert.Equal(FileAttachmentShareOutcome.Private, await new FileAttachmentSharingService(Data).ShareAsync(
            context with { Settings = context.Settings with { ChatResultMode = "none" } }, false, Accept, Allowed, Upload));
        Assert.Equal(0, _uploads); Assert.Equal(0, _confirmations);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
