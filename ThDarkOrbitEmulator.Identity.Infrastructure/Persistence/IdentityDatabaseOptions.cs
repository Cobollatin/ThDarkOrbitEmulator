namespace ThDarkOrbitEmulator.Identity.Infrastructure;

public sealed class IdentityDatabaseOptions
{
    public const string SectionName = "IdentityDatabase";

    public string Schema { get; set; } = "identity";

    public bool AutoMigrate { get; set; } = true;

    public string? RollbackMigrationId { get; set; }

    public string BackupDirectory { get; set; } = "db-backups";

    public bool BackupBeforeMigration { get; set; } = true;

    public string PgDumpExecutable { get; set; } = "pg_dump";

    public bool RequireBackupSuccess { get; set; }

    public bool EnableDockerPgDumpFallback { get; set; } = true;

    public string DockerPostgresContainerName { get; set; } = "data-postgres";
}
