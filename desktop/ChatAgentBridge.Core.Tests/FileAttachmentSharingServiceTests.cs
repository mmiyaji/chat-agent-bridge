using ChatAgentBridge.Core.Sessions;
using ChatAgentBridge.Core.History;

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

    private async Task<FileAttachmentCandidate> CreateCandidateAsync(string name, long length)
    {
        var path = Path.Combine(Workspace, name);
        await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            output.SetLength(length);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input)).ToLowerInvariant();
        var info = new FileInfo(path);
        return new(path, name, info.Length, info.LastWriteTimeUtc, hash);
    }

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
    public async Task FileUploadIntentAndOutcomeCanBeAuditedWithoutBlockingTheTransfer()
    {
        var context = await ContextAsync();
        var audit = new AuditStore(Data);
        async Task Append(string kind) => await audit.AppendAsync(new(DateTimeOffset.UtcNow, kind, null,
            context.Key.Provider, context.Key.ChannelId, context.Key.ThreadId));
        Assert.Equal(FileAttachmentShareOutcome.Sent,
            await new FileAttachmentSharingService(Data).ShareAsync(context, false, Accept, Allowed,
                async (files, token) => { await Append("attachment_approved"); await Upload(files, token); await Append("attachment_sent"); }));
        await Append("attachment_failed");
        Assert.Equal(1, _uploads);
        Assert.Equal(new[] { "attachment_approved", "attachment_failed", "attachment_sent" },
            (await audit.ListAsync()).Select(item => item.Kind).OrderBy(kind => kind).ToArray());
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
    public async Task RevocationOnFinalScopeCheckAfterPreparationPreventsUpload()
    {
        var context = await ContextAsync();
        var checks = 0;
        var result = await new FileAttachmentSharingService(Data).ShareAsync(context, false, Accept,
            _ => Task.FromResult(++checks < 3), Upload);
        Assert.Equal(FileAttachmentShareOutcome.ScopeChanged, result);
        Assert.Equal(3, checks);
        Assert.Equal(0, _uploads);
    }

    [Fact]
    public async Task CancellationDuringConfirmationPreventsUpload()
    {
        var context = await ContextAsync();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileAttachmentSharingService(Data).ShareAsync(context, false,
            (files, _, _) => { cancellation.Cancel(); return Task.FromResult<IReadOnlyList<FileAttachmentCandidate>?>(files); },
            Allowed, Upload, cancellation.Token));
        Assert.Equal(0, _uploads);
    }

    [Theory]
    [InlineData("NONE")]
    [InlineData(" none ")]
    public async Task NoncanonicalPrivateModeBlocksConfirmationAndUpload(string mode)
    {
        var context = await ContextAsync("auto");
        context = context with { Settings = context.Settings with { ChatResultMode = mode } };
        var result = await new FileAttachmentSharingService(Data).ShareAsync(context, false, Accept, Allowed, Upload);
        Assert.Equal(FileAttachmentShareOutcome.Private, result);
        Assert.Equal(0, _confirmations);
        Assert.Equal(0, _uploads);
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

    [Theory]
    [InlineData("count")]
    [InlineData("each")]
    [InlineData("total")]
    public async Task ManualConfirmationCannotExceedConfiguredLimits(string limit)
    {
        var context = await ContextAsync();
        var files = new List<FileAttachmentCandidate>();
        var settings = context.Settings;
        if (limit == "count")
        {
            settings = settings with { FileAttachmentMaxCount = 1 };
            files.Add(context.Candidates[0]);
            files.Add(await CreateCandidateAsync("second.txt", 16));
        }
        else if (limit == "each")
        {
            settings = settings with { FileAttachmentMaxSizeMb = 1 };
            files.Add(await CreateCandidateAsync("over-limit.txt", 1024L * 1024 + 1));
        }
        else
        {
            settings = settings with { FileAttachmentMaxTotalSizeMb = 1 };
            files.Add(await CreateCandidateAsync("first-part.txt", 600 * 1024));
            files.Add(await CreateCandidateAsync("second-part.txt", 600 * 1024));
        }
        context = context with { Settings = settings, Candidates = files };

        await Assert.ThrowsAsync<InvalidDataException>(() => new FileAttachmentSharingService(Data).ShareAsync(context, false,
            (selected, _, _) => { _confirmations++; return Task.FromResult<IReadOnlyList<FileAttachmentCandidate>?>(selected); }, Allowed, Upload));
        Assert.Equal(1, _confirmations);
        Assert.Equal(0, _uploads);
    }

    [Fact]
    public async Task ManualConfirmationCannotForgeAnUnknownCandidate()
    {
        var context = await ContextAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => new FileAttachmentSharingService(Data).ShareAsync(context, false,
            (selected, _, _) => Task.FromResult<IReadOnlyList<FileAttachmentCandidate>?>([selected[0] with { RelativePath = "forged.txt" }]),
            Allowed, Upload));
        Assert.Equal(0, _uploads);
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
