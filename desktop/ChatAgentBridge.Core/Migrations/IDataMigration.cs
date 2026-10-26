namespace ChatAgentBridge.Core.Migrations;

/// <summary>A resumable, idempotent migration over one application's local data directory.</summary>
public interface IDataMigration
{
    string Id { get; }
    int Version { get; }
    Task<bool> IsRequiredAsync(string dataDirectory, CancellationToken cancellationToken);
    Task ExecuteAsync(string dataDirectory, CancellationToken cancellationToken);
    Task ValidateAsync(string dataDirectory, CancellationToken cancellationToken);
}

public enum MigrationPhase
{
    Running,
    Validating,
    Completed
}

public sealed record MigrationProgress(string Id, int Index, int Total, MigrationPhase Phase);
