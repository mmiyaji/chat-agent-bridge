using System.Security.Cryptography;
using System.Text.Json;
using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class IncomingAttachmentPerformanceTests
{
    [Fact]
    public async Task Streaming_hash_covers_sync_and_async_writes_and_async_cleanup_preserves_modified_file()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "cab-incoming-perf-" + Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        try
        {
            SynchronizationContext? downloaderContext = null;
            var callerContext = new SynchronizationContext();
            var previous = SynchronizationContext.Current;
            Task<IncomingAttachmentLease> pending;
            try
            {
                SynchronizationContext.SetSynchronizationContext(callerContext);
                pending = IncomingAttachmentStager.PrepareAsync(workspace, [new("one", "a.txt", 6, null)],
                    async (_, output, _, ct) =>
                    {
                        downloaderContext = SynchronizationContext.Current;
                        output.WriteByte((byte)'a');
                        output.Write("bc"u8.ToArray(), 0, 2);
                        await output.WriteAsync("de"u8.ToArray(), ct);
                        await output.WriteAsync("f"u8.ToArray(), 0, 1, ct);
                    }, recoveryDataDirectory: root);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            var lease = await pending;
            Assert.NotSame(callerContext, downloaderContext);
            var file = Assert.Single(lease.Attachments);
            string manifestPath = Assert.Single(Directory.GetFiles(Path.Combine(root, "incoming-attachments"), "*.json"));
            var manifest = JsonSerializer.Deserialize<IncomingAttachmentRecovery.Manifest>(await File.ReadAllTextAsync(manifestPath))!;
            Assert.Equal(Convert.ToHexString(SHA256.HashData("abcdef"u8)), Assert.Single(manifest.Files).Sha256);
            await File.WriteAllTextAsync(file.Path, "retain"); // same length; cleanup must still hash actual content
            await lease.DisposeAsync();
            Assert.True(File.Exists(file.Path));
            Assert.Equal("retain", await File.ReadAllTextAsync(file.Path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
