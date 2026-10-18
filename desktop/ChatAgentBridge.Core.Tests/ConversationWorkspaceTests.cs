using ChatAgentBridge.Core;
using ChatAgentBridge.Core.Sessions;

namespace ChatAgentBridge.Core.Tests;

public sealed class ConversationWorkspaceTests
{
    [Fact]
    public async Task ChangeWorkspace_ClearsOnlySelectedBindingAndRetainsOverrideAcrossUpdatesAndDetach()
    {
        using var temp = new TempDirectory();
        var original = Directory.CreateDirectory(Path.Combine(temp.Path, "original")).FullName;
        var next = Directory.CreateDirectory(Path.Combine(temp.Path, "next")).FullName;
        var store = new SessionStore(Path.Combine(temp.Path, "store"));
        var one = Key("channel", "thread", "alice");
        var other = Key("channel", "thread", "bob");
        var otherThread = Key("channel", "other", "alice");
        await store.SaveAsync(Binding(one, original));
        await store.SaveAsync(Binding(other, original));
        await store.SaveAsync(Binding(otherThread, original));

        await store.ChangeWorkspaceAsync(one, next, new BridgeSettings { WorkspaceRoot = original, TrustedWorkspaceRoots = temp.Path });

        Assert.Null(await store.FindAsync(one));
        Assert.Equal(GuidFor(other).ToString("D"), (await store.FindAsync(other))!.SessionId);
        Assert.Equal(GuidFor(otherThread).ToString("D"), (await store.FindAsync(otherThread))!.SessionId);
        Assert.Equal(next, await store.FindWorkspaceAsync(one));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.UpdateExecutionPolicyAsync(one, "auto", "auto", "read-only"));
        await store.UpdateModelAsync(other, "gpt-5.4");
        await store.RemoveAsync(one);
        Assert.Equal(next, await store.FindWorkspaceAsync(one));
    }

    [Fact]
    public async Task ChangeWorkspace_SeparatesUsersAndThreads()
    {
        using var temp = new TempDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root")).FullName;
        var next = Directory.CreateDirectory(Path.Combine(temp.Path, "next")).FullName;
        var store = new SessionStore(Path.Combine(temp.Path, "store"));
        var target = Key("c", "t", "alice");
        var user = Key("c", "t", "bob");
        var thread = Key("c", "other", "alice");
        foreach (var key in new[] { target, user, thread }) await store.SaveAsync(Binding(key, root));

        await store.ChangeWorkspaceAsync(target, next, new BridgeSettings { WorkspaceRoot = root, TrustedWorkspaceRoots = temp.Path });

        Assert.Equal(next, await store.FindWorkspaceAsync(target));
        Assert.Null(await store.FindWorkspaceAsync(user));
        Assert.Null(await store.FindWorkspaceAsync(thread));
        Assert.NotNull(await store.FindAsync(user));
        Assert.NotNull(await store.FindAsync(thread));
    }

    [Fact]
    public async Task WritesPreserveOverridesAndLegacyDocumentRoundTrips()
    {
        using var temp = new TempDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root")).FullName;
        var next = Directory.CreateDirectory(Path.Combine(temp.Path, "next")).FullName;
        var storeDir = Path.Combine(temp.Path, "store");
        Directory.CreateDirectory(storeDir);
        var legacyKey = Key("legacy", "thread", "alice");
        await File.WriteAllTextAsync(Path.Combine(storeDir, "sessions.json"), "{\"SchemaVersion\":1,\"Bindings\":[]}");
        var store = new SessionStore(storeDir);
        await store.SaveAsync(Binding(legacyKey, root));
        Assert.Null(await store.FindWorkspaceAsync(legacyKey));

        var target = Key("new", "thread", "alice");
        await store.ChangeWorkspaceAsync(target, next, new BridgeSettings { WorkspaceRoot = root, TrustedWorkspaceRoots = temp.Path });
        await store.UpdateModelAsync(legacyKey, "gpt-5.4");
        var restarted = new SessionStore(storeDir);
        Assert.Equal(next, await restarted.FindWorkspaceAsync(target));
    }

    [Fact]
    public async Task ChangeWorkspace_RejectsUntrustedMissingAndRelativePathsWithoutChanges()
    {
        using var temp = new TempDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(temp.Path, "outside")).FullName;
        var store = new SessionStore(Path.Combine(temp.Path, "store"));
        var key = Key("c", "t", "alice");
        await store.SaveAsync(Binding(key, root));
        var settings = new BridgeSettings { WorkspaceRoot = root };

        await Assert.ThrowsAsync<ArgumentException>(() => store.ChangeWorkspaceAsync(key, outside, settings));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ChangeWorkspaceAsync(key, Path.Combine(temp.Path, "missing"), settings with { TrustedWorkspaceRoots = temp.Path }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ChangeWorkspaceAsync(key, "relative", settings));

        Assert.NotNull(await store.FindAsync(key));
        Assert.Null(await store.FindWorkspaceAsync(key));
    }

    [Fact]
    public async Task ApplyWorkspace_FreezesImplicitTrustAndRejectsRevokedTrust()
    {
        using var temp = new TempDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root")).FullName;
        var child = Directory.CreateDirectory(Path.Combine(root, "child")).FullName;
        var sibling = Directory.CreateDirectory(Path.Combine(temp.Path, "sibling")).FullName;
        var key = Key("c", "t", "alice");
        var store = new SessionStore(Path.Combine(temp.Path, "store"));
        var initial = new BridgeSettings { WorkspaceRoot = root };
        await store.ChangeWorkspaceAsync(key, child, initial);

        var applied = await store.ApplyWorkspaceAsync(key, initial);
        Assert.Equal(child, applied.WorkspaceRoot);
        Assert.Equal(root, applied.TrustedWorkspaceRoots);
        Assert.False(ExecutionPolicy.IsWorkspaceTrusted(applied, sibling));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApplyWorkspaceAsync(key,
            initial with { TrustedWorkspaceRoots = sibling }));
        Assert.Equal(initial, await store.ApplyWorkspaceAsync(Key("other", "t", "alice"), initial));
    }

    private static SessionKey Key(string channel, string thread, string user) => new("slack", channel, thread, user, "slack");
    private static Guid GuidFor(SessionKey key) => key.UserId == "bob" ? new Guid("00000000-0000-0000-0000-000000000002") : new Guid("00000000-0000-0000-0000-000000000003");
    private static SessionBinding Binding(SessionKey key, string workspace) => new(key, workspace, "codex", "exec --json -", GuidFor(key).ToString("D"), DateTimeOffset.UtcNow,
        ChatApprovalMode: "first", FirstApprovalFingerprint: new string('a', 64));

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory() => Path = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))).FullName;
        public string Path { get; }
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
    }
}
