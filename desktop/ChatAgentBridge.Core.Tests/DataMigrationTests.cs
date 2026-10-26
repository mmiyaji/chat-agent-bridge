using System.Text.Json;
using ChatAgentBridge.Core.History;
using ChatAgentBridge.Core.Migrations;

namespace ChatAgentBridge.Core.Tests;

public sealed class DataMigrationTests
{
    [Fact]
    public async Task UnknownCompletedMigrationRefusesAnOlderRegistryBeforeDataChanges()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "data-migrations.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Completed = new[] { new { Id = "future-data-format", Version = 1, CompletedAtUtc = DateTimeOffset.UtcNow } }
        }));
        var current = new FakeMigration("current-format", 1) { Required = true };
        await Assert.ThrowsAsync<InvalidDataException>(() => new DataMigrationCoordinator(directory.Path, [current]).RunAsync());
        Assert.Equal(0, current.ExecuteCount);
        Assert.Equal(0, current.ValidateCount);
    }

    [Fact]
    public async Task PartialFailureResumesAndOnlyValidatedStepsAreCommitted()
    {
        using var directory = new TemporaryDirectory();
        var step = new FakeMigration("fake-step", 1) { Required = true, FailAfterExecuteOnce = true };
        var coordinator = new DataMigrationCoordinator(directory.Path, [step]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RunAsync());
        Assert.False(File.Exists(Path.Combine(directory.Path, "data-migrations.json")));
        Assert.Equal(1, step.ExecuteCount);
        Assert.Equal(0, step.ValidateCount);

        await coordinator.RunAsync();
        Assert.Equal(2, step.ExecuteCount);
        Assert.Equal(1, step.ValidateCount);
        Assert.Contains("fake-step", await File.ReadAllTextAsync(Path.Combine(directory.Path, "data-migrations.json")));

        step.Required = false;
        await coordinator.RunAsync();
        step.Required = true; // Restoring the legacy source must re-run an already-completed step.
        await coordinator.RunAsync();
        step.Required = false;
        await coordinator.RunAsync();
        Assert.Equal(5, step.RequiredCheckCount); // Completed ledger entries never bypass the restored-data check.
        Assert.Equal(3, step.ExecuteCount);
        Assert.Equal(2, step.ValidateCount);
    }

    [Fact]
    public async Task MissingLedgerAfterSourceWasRemovedIsRecoveredByValidation()
    {
        using var directory = new TemporaryDirectory();
        var step = new FakeMigration("crash-window", 1) { Required = true, FailAfterExecuteOnce = true };
        var coordinator = new DataMigrationCoordinator(directory.Path, [step]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RunAsync());
        step.Required = false; // Output exists, but the first attempt failed before ledger commit.
        await coordinator.RunAsync();

        Assert.Equal(1, step.ExecuteCount);
        Assert.Equal(1, step.ValidateCount);
        Assert.True(File.Exists(Path.Combine(directory.Path, "data-migrations.json")));
    }

    [Fact]
    public async Task DependentStepsAreCheckedAfterEarlierStepsRunInTheSamePass()
    {
        using var directory = new TemporaryDirectory();
        var first = new FakeMigration("first", 1) { Required = true };
        var second = new FakeMigration("second", 1)
        {
            RequiredPredicate = path => File.Exists(Path.Combine(path, "first.partial"))
        };
        await new DataMigrationCoordinator(directory.Path, [first, second]).RunAsync();
        Assert.Equal(1, second.ExecuteCount);
        Assert.Equal(1, second.ValidateCount);
    }

    [Fact]
    public async Task ValidationFailureAndCancellationLeaveStepUncommitted()
    {
        using var directory = new TemporaryDirectory();
        var step = new FakeMigration("validate-step", 1) { Required = true, FailValidation = true };
        var coordinator = new DataMigrationCoordinator(directory.Path, [step]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RunAsync());
        Assert.False(File.Exists(Path.Combine(directory.Path, "data-migrations.json")));

        step.FailValidation = false;
        step.CancelDuringExecute = true;
        using var cancellation = new CancellationTokenSource();
        step.Cancellation = cancellation;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RunAsync(cancellationToken: cancellation.Token));
        Assert.False(File.Exists(Path.Combine(directory.Path, "data-migrations.json")));
    }

    [Fact]
    public async Task MalformedLedgerFailsBeforeAnyMigrationCallback()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "data-migrations.json"), "{\"SchemaVersion\":9,\"Completed\":[]}");
        var step = new FakeMigration("untouched", 1) { Required = true };
        var coordinator = new DataMigrationCoordinator(directory.Path, [step]);

        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.RunAsync());
        Assert.Equal(0, step.RequiredCheckCount);
        Assert.Equal(0, step.ExecuteCount);
    }

    [Fact]
    public async Task UnrecordedCompletedOutputIsValidatedAndRecordedWithoutProgress()
    {
        using var directory = new TemporaryDirectory();
        var step = new FakeMigration("not-required", 1) { Required = false };
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "not-required.partial"), "existing output");
        var progress = new RecordingProgress();
        await new DataMigrationCoordinator(directory.Path, [step]).RunAsync(progress);
        Assert.Empty(progress.Items);
        Assert.Equal(1, step.ValidateCount);
        Assert.True(File.Exists(Path.Combine(directory.Path, "data-migrations.json")));
    }

    [Fact]
    public void RegistryRejectsInvalidIdsDuplicateIdsAndNonpositiveVersions()
    {
        using var directory = new TemporaryDirectory();
        Assert.Throws<ArgumentException>(() => new DataMigrationCoordinator(directory.Path, [new FakeMigration("Bad ID", 1)]));
        Assert.Throws<ArgumentException>(() => new DataMigrationCoordinator(directory.Path,
            [new FakeMigration("same", 1), new FakeMigration("same", 2)]));
        Assert.Throws<ArgumentException>(() => new DataMigrationCoordinator(directory.Path, [new FakeMigration("valid", 0)]));
    }

    [Fact]
    public async Task VersionUpgradeValidatesAndAdvancesMarkerWhenNoWorkIsRequired()
    {
        using var directory = new TemporaryDirectory();
        var step = new FakeMigration("upgrade-step", 1) { Required = true };
        await new DataMigrationCoordinator(directory.Path, [step]).RunAsync();
        step.VersionValue = 2;
        step.Required = false;
        await new DataMigrationCoordinator(directory.Path, [step]).RunAsync();
        Assert.Equal(1, step.ExecuteCount);
        Assert.Equal(2, step.ValidateCount);
        Assert.Contains("\"Version\": 2", await File.ReadAllTextAsync(Path.Combine(directory.Path, "data-migrations.json")));
    }

    [Fact]
    public async Task AuditJsonlMigrationImportsLegacyRowsAndValidatesTheSegmentStore()
    {
        using var directory = new TemporaryDirectory();
        var record = new AuditEvent(DateTimeOffset.UtcNow, "completed", Guid.NewGuid(), "local", "", "");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, Records = new[] { record } });
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "audit.json"), [.. bytes, (byte)'\n']);
        var migration = new AuditJsonlMigration();
        await new DataMigrationCoordinator(directory.Path, [migration]).RunAsync();

        Assert.False(File.Exists(Path.Combine(directory.Path, "audit.json")));
        Assert.Equal(record, Assert.Single(await new AuditStore(directory.Path).ListAsync()));
        Assert.True(File.Exists(Path.Combine(directory.Path, "data-migrations.json")));
    }

    private sealed class FakeMigration(string id, int version) : IDataMigration
    {
        public string Id { get; } = id;
        public int Version => VersionValue;
        public int VersionValue { get; set; } = version;
        public bool Required { get; set; }
        public Func<string, bool>? RequiredPredicate { get; set; }
        public bool FailAfterExecuteOnce { get; set; }
        public bool FailValidation { get; set; }
        public bool CancelDuringExecute { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
        public int RequiredCheckCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public int ValidateCount { get; private set; }

        public Task<bool> IsRequiredAsync(string dataDirectory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequiredCheckCount++;
            return Task.FromResult(RequiredPredicate?.Invoke(dataDirectory) ?? Required);
        }

        public async Task ExecuteAsync(string dataDirectory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCount++;
            var marker = Path.Combine(dataDirectory, $"{Id}.partial");
            if (!File.Exists(marker)) await File.WriteAllTextAsync(marker, "partial", cancellationToken);
            if (FailAfterExecuteOnce)
            {
                FailAfterExecuteOnce = false;
                throw new InvalidOperationException("Simulated migration interruption.");
            }
            if (CancelDuringExecute) Cancellation!.Cancel();
        }

        public Task ValidateAsync(string dataDirectory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCount++;
            if (FailValidation) throw new InvalidOperationException("Simulated validation failure.");
            if (!File.Exists(Path.Combine(dataDirectory, $"{Id}.partial"))) throw new InvalidDataException("Migration output missing.");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProgress : IProgress<MigrationProgress>
    {
        public List<MigrationProgress> Items { get; } = [];
        public void Report(MigrationProgress value) => Items.Add(value);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
