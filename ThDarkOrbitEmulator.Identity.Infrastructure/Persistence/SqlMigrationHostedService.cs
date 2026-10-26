using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ThDarkOrbitEmulator.Identity.Infrastructure;

public sealed class SqlMigrationHostedService(
    IConfiguration configuration,
    IOptions<IdentityDatabaseOptions> options,
    ILogger<SqlMigrationHostedService> logger) : IHostedService
{
    private readonly IdentityDatabaseOptions _options = options.Value;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.AutoMigrate)
        {
            return;
        }

        var writeConnectionString = configuration.GetConnectionString("identity-write");
        if (string.IsNullOrWhiteSpace(writeConnectionString))
        {
            throw new InvalidOperationException("Missing connection string 'identity-write'.");
        }

        await using var connection = new NpgsqlConnection(writeConnectionString);
        await connection.OpenAsync(cancellationToken);

        await EnsureMigrationHistoryTableAsync(connection, cancellationToken);
        await RollbackIfConfiguredAsync(connection, cancellationToken);

        var migrationPath = Path.Combine(AppContext.BaseDirectory, "Migrations", "Sql");
        if (!Directory.Exists(migrationPath))
        {
            logger.LogInformation("No SQL migration directory found at '{Path}'.", migrationPath);
            return;
        }

        foreach (var upFile in Directory.GetFiles(migrationPath, "*.up.pgsql").OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
        {
            var migrationId = Path.GetFileName(upFile).Replace(".up.pgsql", string.Empty, StringComparison.OrdinalIgnoreCase);
            if (await IsMigrationAppliedAsync(connection, migrationId, cancellationToken))
            {
                continue;
            }

            await BackupDatabaseAsync(writeConnectionString, migrationId, cancellationToken);

            var sql = await File.ReadAllTextAsync(upFile, cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                await command.ExecuteNonQueryAsync(cancellationToken);

                await using var insertCommand = new NpgsqlCommand($"""
                    INSERT INTO {_options.Schema}.schema_migrations (migration_id, applied_on_utc)
                    VALUES (@migrationId, NOW() AT TIME ZONE 'UTC');
                    """, connection, transaction);
                insertCommand.Parameters.AddWithValue("migrationId", migrationId);
                await insertCommand.ExecuteNonQueryAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);
                logger.LogInformation("Applied migration '{MigrationId}'.", migrationId);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                await TryExecuteDownScriptAsync(connection, migrationPath, migrationId, cancellationToken);
                throw;
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task EnsureMigrationHistoryTableAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            CREATE SCHEMA IF NOT EXISTS {_options.Schema};
            CREATE TABLE IF NOT EXISTS {_options.Schema}.schema_migrations
            (
                migration_id TEXT PRIMARY KEY,
                applied_on_utc TIMESTAMP NOT NULL
            );
            """, connection);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> IsMigrationAppliedAsync(NpgsqlConnection connection, string migrationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT EXISTS (
                SELECT 1
                FROM {_options.Schema}.schema_migrations
                WHERE migration_id = @migrationId
            );
            """, connection);

        command.Parameters.AddWithValue("migrationId", migrationId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private async Task RollbackIfConfiguredAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.RollbackMigrationId))
        {
            return;
        }

        var migrationPath = Path.Combine(AppContext.BaseDirectory, "Migrations", "Sql");
        var downScript = Path.Combine(migrationPath, $"{_options.RollbackMigrationId}.down.pgsql");
        if (!File.Exists(downScript))
        {
            throw new FileNotFoundException($"Rollback script not found for migration '{_options.RollbackMigrationId}'.", downScript);
        }

        var sql = await File.ReadAllTextAsync(downScript, cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var downCommand = new NpgsqlCommand(sql, connection, transaction);
        await downCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var deleteCommand = new NpgsqlCommand($"""
            DELETE FROM {_options.Schema}.schema_migrations
            WHERE migration_id = @migrationId;
            """, connection, transaction);
        deleteCommand.Parameters.AddWithValue("migrationId", _options.RollbackMigrationId);
        await deleteCommand.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task TryExecuteDownScriptAsync(NpgsqlConnection connection, string migrationPath, string migrationId, CancellationToken cancellationToken)
    {
        var downScript = Path.Combine(migrationPath, $"{migrationId}.down.pgsql");
        if (!File.Exists(downScript))
        {
            return;
        }

        var sql = await File.ReadAllTextAsync(downScript, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task BackupDatabaseAsync(string connectionString, string migrationId, CancellationToken cancellationToken)
    {
        if (!_options.BackupBeforeMigration)
        {
            logger.LogInformation("Backup disabled for migration '{MigrationId}'.", migrationId);
            return;
        }

        Directory.CreateDirectory(_options.BackupDirectory);

        var backupName = $"identity-{DateTime.UtcNow:yyyyMMddHHmmss}-{migrationId}.dump";
        var backupPath = Path.Combine(_options.BackupDirectory, backupName);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _options.PgDumpExecutable,
                Arguments = $"--format=custom --file \"{backupPath}\" \"{connectionString}\"",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        bool started;
        try
        {
            started = process.Start();
        }
        catch (Win32Exception ex) when (!_options.RequireBackupSuccess)
        {
            if (_options.EnableDockerPgDumpFallback && await TryBackupWithDockerAsync(connectionString, backupPath, cancellationToken))
            {
                logger.LogInformation("Backup completed for migration '{MigrationId}' using docker pg_dump fallback.", migrationId);
                return;
            }

            logger.LogWarning(ex,
                "Skipping backup for migration '{MigrationId}' because '{Executable}' was not found. " +
                "Set IdentityDatabase:PgDumpExecutable to an absolute path or set RequireBackupSuccess=true.",
                migrationId,
                _options.PgDumpExecutable);
            return;
        }

        if (!started)
        {
            const string message = "Unable to start pg_dump process for database backup.";
            if (_options.RequireBackupSuccess)
            {
                throw new InvalidOperationException(message);
            }

            if (_options.EnableDockerPgDumpFallback && await TryBackupWithDockerAsync(connectionString, backupPath, cancellationToken))
            {
                logger.LogInformation("Backup completed for migration '{MigrationId}' using docker pg_dump fallback.", migrationId);
                return;
            }

            logger.LogWarning("{Message} Skipping backup for migration '{MigrationId}'.", message, migrationId);
            return;
        }

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            if (_options.RequireBackupSuccess)
            {
                throw new InvalidOperationException($"Database backup failed before migration '{migrationId}'. {error}");
            }

            logger.LogWarning("Database backup failed before migration '{MigrationId}' and will be skipped. {Error}", migrationId, error);
        }
    }

    private async Task<bool> TryBackupWithDockerAsync(string connectionString, string backupPath, CancellationToken cancellationToken)
    {
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(connectionString);
            var containerName = string.IsNullOrWhiteSpace(_options.DockerPostgresContainerName)
                ? "postgres"
                : _options.DockerPostgresContainerName;

            var args =
                $"exec -e PGPASSWORD=\"{connection.Password}\" {containerName} pg_dump --username=\"{connection.Username}\" --dbname=\"{connection.Database}\" --format=custom --no-owner --no-privileges";

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = args,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                return false;
            }

            await using (var fileStream = File.Create(backupPath))
            {
                await process.StandardOutput.BaseStream.CopyToAsync(fileStream, cancellationToken);
            }

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
            {
                return true;
            }

            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            logger.LogWarning("Docker pg_dump fallback failed. {Error}", error);
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Docker pg_dump fallback is unavailable.");
            return false;
        }
    }
}
