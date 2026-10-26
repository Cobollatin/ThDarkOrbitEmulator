using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.Identity.Application;
using ThDarkOrbitEmulator.Identity.Infrastructure.Keycloak;

namespace ThDarkOrbitEmulator.Identity.Infrastructure;

public sealed class PlatformOwnerSeedHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<PlatformOwnerSeedOptions> options,
    ILogger<PlatformOwnerSeedHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var seed = options.Value;
        var role = string.IsNullOrWhiteSpace(seed.RealmRole)
            ? KeycloakRbacDefinitions.AdminRole
            : seed.RealmRole.Trim();

        var accountsToSeed = new List<(string Username, string Password, string? Email, string Role)>();
        if (!string.IsNullOrWhiteSpace(seed.Username) && !string.IsNullOrWhiteSpace(seed.Password))
        {
            accountsToSeed.Add((seed.Username.Trim(), seed.Password, seed.Email?.Trim(), role));
        }

        if (!string.IsNullOrWhiteSpace(seed.AdminUsername)
            && !string.IsNullOrWhiteSpace(seed.AdminPassword)
            && !string.Equals(seed.AdminUsername.Trim(), seed.Username?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            accountsToSeed.Add((seed.AdminUsername.Trim(), seed.AdminPassword, seed.AdminEmail?.Trim(), role));
        }

        if (accountsToSeed.Count == 0)
        {
            logger.LogInformation("PlatformOwnerSeed is not configured; skipping platform owner seed.");
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var keycloakService = scope.ServiceProvider.GetRequiredService<IKeycloakService>();
            var identityCommandService = scope.ServiceProvider.GetRequiredService<IIdentityCommandService>();
            var identityQueryService = scope.ServiceProvider.GetRequiredService<IIdentityQueryService>();
            var writeDbContext = scope.ServiceProvider.GetService<IdentityWriteDbContext>();

            foreach (var (username, password, email, accountRole) in accountsToSeed)
            {
                await SeedAdminAccountAsync(
                    keycloakService,
                    identityCommandService,
                    identityQueryService,
                    writeDbContext,
                    username,
                    password,
                    email,
                    accountRole,
                    seed.DefaultCompany,
                    seed.SeedRichStartingState,
                    cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Platform owner seed failed; startup will continue.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SeedAdminAccountAsync(
        IKeycloakService keycloakService,
        IIdentityCommandService identityCommandService,
        IIdentityQueryService identityQueryService,
        IdentityWriteDbContext? writeDbContext,
        string username,
        string password,
        string? email,
        string role,
        string? defaultCompany,
        bool seedRichStartingState,
        CancellationToken cancellationToken)
    {
        var keycloakCreated = await keycloakService.CreateUserAsync(
            username,
            password,
            email,
            cancellationToken,
            role);
        if (!keycloakCreated)
        {
            logger.LogWarning("Could not ensure platform admin '{Username}' in Keycloak.", username);
        }
        else
        {
            await keycloakService.EnsureRealmRoleAsync(username, role, cancellationToken);
        }

        var existing = await identityQueryService.ExecuteAsync(new GetAccountByUsernameQuery(username), cancellationToken);
        if (existing is null)
        {
            var created = await identityCommandService.ExecuteAsync(
                new RegisterAccountCommand(username, email, password),
                cancellationToken);
            if (created)
            {
                logger.LogInformation(
                    "Seeded platform admin '{Username}' with role '{Role}'.",
                    username,
                    role);
            }

            existing = await identityQueryService.ExecuteAsync(new GetAccountByUsernameQuery(username), cancellationToken);
        }
        else
        {
            logger.LogDebug("Platform admin '{Username}' already exists in Identity database.", username);
        }

        if (existing is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(existing.Company) && !string.IsNullOrWhiteSpace(defaultCompany))
        {
            await identityCommandService.SetCompanyAsync(existing.Id, defaultCompany.Trim().ToLowerInvariant(), cancellationToken);
        }

        if (seedRichStartingState && writeDbContext is not null && writeDbContext.Database.IsRelational())
        {
            await ProvisionAdminEconomyStateAsync(writeDbContext, existing.Id, cancellationToken);
        }
    }

    internal static async Task ProvisionAdminEconomyStateAsync(
        IdentityWriteDbContext dbContext,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DO $$
            DECLARE
                v_account_id uuid := {0};
            BEGIN
                IF to_regclass('economy.account_imports') IS NULL THEN
                    RETURN;
                END IF;

                INSERT INTO economy.account_imports (account_id)
                VALUES (v_account_id)
                ON CONFLICT (account_id) DO NOTHING;

                IF to_regclass('economy.hangar_equipment_imports') IS NOT NULL THEN
                    INSERT INTO economy.hangar_equipment_imports (account_id)
                    VALUES (v_account_id)
                    ON CONFLICT (account_id) DO NOTHING;
                END IF;

                IF to_regclass('economy.tech_factory_legacy_imports') IS NOT NULL THEN
                    INSERT INTO economy.tech_factory_legacy_imports (account_id)
                    VALUES (v_account_id)
                    ON CONFLICT (account_id) DO NOTHING;
                END IF;

                CREATE TABLE IF NOT EXISTS economy.resource_imports (
                    account_id uuid NOT NULL REFERENCES identity.accounts(id),
                    asset_id text NOT NULL REFERENCES economy.assets(id),
                    imported_at timestamptz NOT NULL DEFAULT now(),
                    PRIMARY KEY (account_id, asset_id)
                );

                INSERT INTO economy.resource_imports (account_id, asset_id)
                SELECT v_account_id, id FROM economy.assets WHERE kind = 'resource'
                ON CONFLICT (account_id, asset_id) DO NOTHING;

                INSERT INTO economy.balances (account_id, asset_id, amount)
                SELECT v_account_id, v.asset_id, v.amount
                FROM (VALUES
                    ('credits', 10000000::bigint),
                    ('uridium', 1000000::bigint),
                    ('experience', 5000000::bigint),
                    ('honor', 500000::bigint),
                    ('assembly_components', 100000::bigint),
                    ('tech_components', 100000::bigint),
                    ('galaxy_energy', 10000::bigint),
                    ('prometium', 50000::bigint),
                    ('endurium', 50000::bigint),
                    ('terbium', 50000::bigint),
                    ('speed_boost', 50::bigint),
                    ('energy_charge', 50::bigint)
                ) AS v(asset_id, amount)
                WHERE EXISTS (SELECT 1 FROM economy.assets a WHERE a.id = v.asset_id)
                ON CONFLICT (account_id, asset_id)
                DO UPDATE SET amount = GREATEST(economy.balances.amount, EXCLUDED.amount);

                INSERT INTO economy.ledger (account_id, asset_id, delta, balance_after, reason, menu_source, idempotency_key)
                SELECT v_account_id, v.asset_id, v.amount, v.amount, 'admin_seed', 'admin', 'seed:admin:' || v.asset_id
                FROM (VALUES
                    ('credits', 10000000::bigint),
                    ('uridium', 1000000::bigint),
                    ('experience', 5000000::bigint),
                    ('honor', 500000::bigint),
                    ('assembly_components', 100000::bigint),
                    ('tech_components', 100000::bigint),
                    ('galaxy_energy', 10000::bigint),
                    ('prometium', 50000::bigint),
                    ('endurium', 50000::bigint),
                    ('terbium', 50000::bigint),
                    ('speed_boost', 50::bigint),
                    ('energy_charge', 50::bigint)
                ) AS v(asset_id, amount)
                WHERE EXISTS (SELECT 1 FROM economy.assets a WHERE a.id = v.asset_id)
                ON CONFLICT (account_id, idempotency_key, asset_id) DO NOTHING;

                INSERT INTO economy.inventory (account_id, item_id, quantity)
                SELECT v_account_id, v.item_id, v.quantity
                FROM (VALUES
                    ('laser_lf3', 20::bigint),
                    ('laser_lf2', 10::bigint),
                    ('shield_sg3n_b02', 20::bigint),
                    ('shield_sg3n_a03', 10::bigint),
                    ('drone_iris', 8::bigint),
                    ('drone_flax', 4::bigint),
                    ('pet_10', 1::bigint),
                    ('extra_cpu', 4::bigint)
                ) AS v(item_id, quantity)
                WHERE EXISTS (SELECT 1 FROM economy.items i WHERE i.id = v.item_id)
                ON CONFLICT (account_id, item_id)
                DO UPDATE SET quantity = GREATEST(economy.inventory.quantity, EXCLUDED.quantity);

                IF to_regclass('economy.hangar_selections') IS NOT NULL THEN
                    INSERT INTO economy.hangar_selections (account_id, active_slot)
                    VALUES (v_account_id, 1)
                    ON CONFLICT (account_id) DO NOTHING;
                END IF;

                IF to_regclass('economy.hangar_equipment_assignments') IS NOT NULL THEN
                    INSERT INTO economy.hangar_equipment_assignments (account_id, hangar_slot, configuration, item_id, quantity)
                    SELECT v_account_id, v.hangar_slot, v.configuration, v.item_id, v.quantity
                    FROM (VALUES
                        (1, 1, 'laser_lf3', 5),
                        (1, 1, 'shield_sg3n_b02', 12),
                        (1, 1, 'drone_iris', 8),
                        (1, 1, 'pet_10', 1),
                        (1, 1, 'extra_cpu', 2),
                        (1, 2, 'laser_lf3', 5),
                        (1, 2, 'shield_sg3n_b02', 12),
                        (1, 2, 'drone_iris', 8),
                        (1, 2, 'pet_10', 1),
                        (1, 2, 'extra_cpu', 2),
                        (2, 1, 'laser_lf3', 1),
                        (2, 1, 'shield_sg3n_b02', 1),
                        (2, 1, 'drone_iris', 8),
                        (2, 1, 'pet_10', 1),
                        (2, 1, 'extra_cpu', 1),
                        (2, 2, 'laser_lf3', 1),
                        (2, 2, 'shield_sg3n_b02', 1),
                        (2, 2, 'drone_iris', 8),
                        (2, 2, 'pet_10', 1),
                        (2, 2, 'extra_cpu', 1)
                    ) AS v(hangar_slot, configuration, item_id, quantity)
                    WHERE EXISTS (SELECT 1 FROM economy.items i WHERE i.id = v.item_id)
                    ON CONFLICT (account_id, hangar_slot, configuration, item_id) DO NOTHING;
                END IF;

                IF to_regclass('economy.skylab_collectors') IS NOT NULL THEN
                    INSERT INTO economy.skylab_collectors (account_id, collector_id, level, last_collected)
                    VALUES
                        (v_account_id, 'prometium', 10, now()),
                        (v_account_id, 'endurium', 10, now()),
                        (v_account_id, 'terbium', 10, now())
                    ON CONFLICT (account_id, collector_id) DO NOTHING;
                END IF;

                IF to_regclass('economy.galaxy_gates') IS NOT NULL THEN
                    INSERT INTO economy.galaxy_gates (account_id, gate_id, parts_owned, activated)
                    VALUES
                        (v_account_id, 'alpha', 100, true),
                        (v_account_id, 'beta', 100, true),
                        (v_account_id, 'gamma', 100, true),
                        (v_account_id, 'delta', 100, true)
                    ON CONFLICT (account_id, gate_id) DO NOTHING;
                END IF;
            END $$;
            """;

        // PostgreSQL does not bind parameters inside a DO block. accountId is a
        // typed Guid, so its canonical hexadecimal representation is safe as a SQL literal.
        var accountSqlLiteral = $"'{accountId:D}'::uuid";
        await dbContext.Database.ExecuteSqlRawAsync(
            sql.Replace("{0}", accountSqlLiteral, StringComparison.Ordinal), cancellationToken);
    }
}
