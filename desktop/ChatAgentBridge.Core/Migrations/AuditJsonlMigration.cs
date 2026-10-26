using ChatAgentBridge.Core.History;

namespace ChatAgentBridge.Core.Migrations;

/// <summary>Migrates the legacy single-file audit store into validated JSONL segments.</summary>
public sealed class AuditJsonlMigration : IDataMigration
{
    public string Id => "audit-jsonl";
    public int Version => 1;

    public Task<bool> IsRequiredAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.Combine(Path.GetFullPath(dataDirectory), "audit.json");
        try
        {
            var attributes = File.GetAttributes(source);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Legacy audit data cannot be a symbolic link or reparse point.");
            if ((attributes & FileAttributes.Directory) != 0)
                throw new InvalidDataException("Legacy audit path is a directory, not a file.");
            return Task.FromResult(true);
        }
        catch (FileNotFoundException) { return Task.FromResult(false); }
        catch (DirectoryNotFoundException) { return Task.FromResult(false); }
    }

    public async Task ExecuteAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        _ = await new AuditStore(dataDirectory).ListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ValidateAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.Combine(Path.GetFullPath(dataDirectory), "audit.json");
        try
        {
            _ = File.GetAttributes(source);
            throw new InvalidDataException("Legacy audit data remains after JSONL migration.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        var fullDirectory = Path.GetFullPath(dataDirectory);
        var manifest = Path.Combine(fullDirectory, "audit-manifest.json");
        var hasManifest = false;
        try
        {
            var attributes = File.GetAttributes(manifest);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) != 0)
                throw new InvalidDataException("Audit manifest path is not a regular file.");
            hasManifest = true;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        var hasSegments = false;
        try { hasSegments = Directory.GetFiles(fullDirectory, "audit-segment-*.jsonl").Length > 0; }
        catch (DirectoryNotFoundException) { }
        if (hasManifest || hasSegments)
            _ = await new AuditStore(fullDirectory).ListAsync(cancellationToken).ConfigureAwait(false);
    }
}
