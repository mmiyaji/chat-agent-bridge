namespace ChatAgentBridge.Core.Migrations;

/// <summary>Register future application data upgrades here, in dependency order.</summary>
public static class ApplicationDataMigrations
{
    public static IReadOnlyList<IDataMigration> Create() => [new AuditJsonlMigration()];
}
