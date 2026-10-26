using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart;
using ThDarkOrbitEmulator.ApiService.Features.IndexInternal.InternalStart.Contracts;
using ThDarkOrbitEmulator.ApiService.Features.Resources.Maps;
using ThDarkOrbitEmulator.Identity.Application;
using ThDarkOrbitEmulator.Identity.Domain;
using ThDarkOrbitEmulator.Identity.Infrastructure;
using ThDarkOrbitEmulator.Identity.Infrastructure.Keycloak;

namespace ThDarkOrbitEmulator.Tests.Identity;

// SPEC-03: docs/specs/03-identity-companies-and-onboarding.md
public class IdentityAndOnboardingSpecificationTests
{
    private static readonly string[] CompanyCodes = ["mmo", "eic", "vru", "tst"];

    // ---- §2 Account domain invariants ----

    [Test]
    public void Account_DefaultsToFreshIdentityWithoutCompany()
    {
        var first = new Account();
        var second = new Account();

        Assert.Multiple(() =>
        {
            Assert.That(first.Id, Is.Not.EqualTo(Guid.Empty));
            Assert.That(first.Id, Is.Not.EqualTo(second.Id));
            Assert.That(first.Username, Is.Empty);
            Assert.That(first.Email, Is.Null);
            Assert.That(first.GameUserId, Is.Zero);
            Assert.That(first.Company, Is.Null);
        });
    }

    [Test]
    public void Account_ExposesOnlyTheSpecifiedPersistentShape()
    {
        var properties = typeof(Account).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(property => property.Name, property => property.PropertyType);

        Assert.That(properties, Is.EquivalentTo(new Dictionary<string, Type>
        {
            ["Id"] = typeof(Guid),
            ["Username"] = typeof(string),
            ["Email"] = typeof(string),
            ["GameUserId"] = typeof(long),
            ["Company"] = typeof(string)
        }));
        Assert.That(typeof(Account).IsSealed, Is.True);
    }

    [Test]
    public void IdentityWriteModel_EnforcesColumnLimitsAndUniqueIndexes()
    {
        using var context = CreateWriteContext(Guid.NewGuid().ToString());
        var entity = context.Model.FindEntityType(typeof(Account))!;

        Assert.Multiple(() =>
        {
            Assert.That(entity.GetTableName(), Is.EqualTo("accounts"));
            Assert.That(entity.GetSchema(), Is.EqualTo("identity"));
            Assert.That(entity.FindProperty(nameof(Account.Username))!.GetMaxLength(), Is.EqualTo(128));
            Assert.That(entity.FindProperty(nameof(Account.Username))!.IsNullable, Is.False);
            Assert.That(entity.FindProperty(nameof(Account.Email))!.GetMaxLength(), Is.EqualTo(256));
            Assert.That(entity.FindProperty(nameof(Account.Email))!.IsNullable, Is.True);
            Assert.That(entity.FindProperty(nameof(Account.Company))!.GetMaxLength(), Is.EqualTo(3));
            Assert.That(entity.FindProperty(nameof(Account.Company))!.IsNullable, Is.True);
            Assert.That(entity.FindProperty(nameof(Account.GameUserId))!.IsNullable, Is.False);

            var uniqueIndexes = entity.GetIndexes().Where(index => index.IsUnique)
                .Select(index => index.Properties.Single().Name).ToArray();
            Assert.That(uniqueIndexes, Is.EquivalentTo(new[] { nameof(Account.Username), nameof(Account.GameUserId) }));
            Assert.That(entity.FindPrimaryKey()!.Properties.Single().Name, Is.EqualTo(nameof(Account.Id)));
        });
    }

    // ---- §3 Registration, duplicates and game user IDs ----

    [Test]
    public async Task Register_NewUsername_StoresTrimmedNameEmailAndSequenceId()
    {
        var (write, read, generator) = CreateServices();
        var commands = new IdentityCommandService(write, generator);

        var created = await commands.ExecuteAsync(new RegisterAccountCommand("  pilot  ", "pilot@test", "pw"), CancellationToken.None);

        var stored = await write.Accounts.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(created, Is.True);
            Assert.That(stored.Username, Is.EqualTo("pilot"));
            Assert.That(stored.Email, Is.EqualTo("pilot@test"));
            Assert.That(stored.GameUserId, Is.EqualTo(100_000_000));
            Assert.That(stored.Company, Is.Null);
            Assert.That(generator.Calls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Register_DoesNotPersistThePassword()
    {
        var (write, _, generator) = CreateServices();
        var commands = new IdentityCommandService(write, generator);

        await commands.ExecuteAsync(new RegisterAccountCommand("pilot", null, "s3cret-value"), CancellationToken.None);

        var stored = await write.Accounts.SingleAsync();
        var values = typeof(Account).GetProperties().Select(property => property.GetValue(stored)?.ToString() ?? string.Empty);
        Assert.That(values, Has.None.Contains("s3cret-value"));
    }

    [TestCase("pilot", "pilot")]
    [TestCase("pilot", " pilot")]
    [TestCase("pilot", "pilot\t")]
    [TestCase(" pilot ", "pilot")]
    public async Task Register_DuplicateUsernameAfterTrim_IsRejectedWithoutConsumingAnId(string first, string second)
    {
        var (write, _, generator) = CreateServices();
        var commands = new IdentityCommandService(write, generator);

        Assert.That(await commands.ExecuteAsync(new RegisterAccountCommand(first, null, "pw"), CancellationToken.None), Is.True);
        Assert.That(await commands.ExecuteAsync(new RegisterAccountCommand(second, "other@test", "pw"), CancellationToken.None), Is.False);

        Assert.That(await write.Accounts.CountAsync(), Is.EqualTo(1));
        Assert.That(generator.Calls, Is.EqualTo(1), "A rejected duplicate must not draw from game_user_id_seq.");
    }

    [Test]
    public async Task Register_UsernameComparisonIsOrdinalCaseSensitive()
    {
        var (write, _, generator) = CreateServices();
        var commands = new IdentityCommandService(write, generator);

        Assert.That(await commands.ExecuteAsync(new RegisterAccountCommand("Pilot", null, "pw"), CancellationToken.None), Is.True);
        Assert.That(await commands.ExecuteAsync(new RegisterAccountCommand("pilot", null, "pw"), CancellationToken.None), Is.True);

        Assert.That(await write.Accounts.Select(a => a.Username).ToListAsync(), Is.EquivalentTo(new[] { "Pilot", "pilot" }));
    }

    [Test]
    public async Task Register_DuplicateEmail_IsNotAnIdentityConflict()
    {
        // Current contract (SPEC-03 §3.3): email is optional, not unique, and stored verbatim.
        var (write, _, generator) = CreateServices();
        var commands = new IdentityCommandService(write, generator);

        Assert.That(await commands.ExecuteAsync(new RegisterAccountCommand("alpha", "same@test", "pw"), CancellationToken.None), Is.True);
        Assert.That(await commands.ExecuteAsync(new RegisterAccountCommand("beta", "same@test", "pw"), CancellationToken.None), Is.True);
        Assert.That(await commands.ExecuteAsync(new RegisterAccountCommand("gamma", null, "pw"), CancellationToken.None), Is.True);

        Assert.That(await write.Accounts.CountAsync(a => a.Email == "same@test"), Is.EqualTo(2));
        Assert.That(await write.Accounts.CountAsync(a => a.Email == null), Is.EqualTo(1));
    }

    [Test]
    public async Task Register_DuplicateUsername_DoesNotOverwriteTheExistingAccount()
    {
        var (write, _, generator) = CreateServices();
        var commands = new IdentityCommandService(write, generator);
        Assert.That(await commands.ExecuteAsync(new RegisterAccountCommand("pilot", "first@test", "pw"), CancellationToken.None), Is.True);
        var original = await write.Accounts.SingleAsync();
        var originalId = original.Id;
        original.Company = "eic";
        await write.SaveChangesAsync();

        Assert.That(await commands.ExecuteAsync(new RegisterAccountCommand(" pilot ", "second@test", "other"), CancellationToken.None), Is.False);
        var stored = await write.Accounts.AsNoTracking().SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(stored.Id, Is.EqualTo(originalId));
            Assert.That(stored.Email, Is.EqualTo("first@test"));
            Assert.That(stored.GameUserId, Is.EqualTo(100_000_000));
            Assert.That(stored.Company, Is.EqualTo("eic"));
            Assert.That(generator.Calls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Register_GameUserIdsFollowTheSequenceAndAreUnique()
    {
        var (write, _, generator) = CreateServices();
        var commands = new IdentityCommandService(write, generator);

        foreach (var name in new[] { "a", "b", "a", "c", "d" })
            await commands.ExecuteAsync(new RegisterAccountCommand(name, null, "pw"), CancellationToken.None);

        var ids = await write.Accounts.OrderBy(a => a.GameUserId).Select(a => a.GameUserId).ToListAsync();
        Assert.That(ids, Is.EqualTo(new long[] { 100_000_000, 100_000_001, 100_000_002, 100_000_003 }));
        Assert.That(generator.Calls, Is.EqualTo(4));
    }

    [Test]
    public void GameUserIdMigrations_StartTheSequenceAtOneHundredMillion()
    {
        foreach (var file in new[] { "002_add_game_user_id.up.pgsql", "003_ensure_game_user_id_default.up.pgsql" })
        {
            var sql = ReadMigration(file);
            Assert.That(sql, Does.Contain("CREATE SEQUENCE IF NOT EXISTS identity.game_user_id_seq START WITH 100000000 INCREMENT BY 1"), file);
            Assert.That(sql, Does.Contain("SET DEFAULT nextval('identity.game_user_id_seq')"), file);
        }
        Assert.That(ReadMigration("002_add_game_user_id.up.pgsql"),
            Does.Contain("CREATE UNIQUE INDEX IF NOT EXISTS ux_identity_accounts_game_user_id"));
    }

    [TestCase("identity")]
    [TestCase("_identity2")]
    [TestCase("Identity_Schema")]
    public void PostgresIdentifier_AcceptsSafeSchemaNames(string schema)
        => Assert.That(PostgresIdentifier.RequireSchema(schema), Is.EqualTo(schema));

    [TestCase("")]
    [TestCase(" ")]
    [TestCase("1identity")]
    [TestCase("identity;drop")]
    [TestCase("identity.accounts")]
    [TestCase("iden tity")]
    [TestCase("identity'")]
    public void PostgresIdentifier_RejectsUnsafeSchemaNames(string schema)
        => Assert.Throws<InvalidOperationException>(() => PostgresIdentifier.RequireSchema(schema));

    // ---- §3.4 Credential queries ----

    [Test]
    public async Task Queries_FindAccountsByTrimmedUsernameOnly()
    {
        var (write, read, generator) = CreateServices();
        var commands = new IdentityCommandService(write, generator);
        var queries = new IdentityQueryService(read);
        await commands.ExecuteAsync(new RegisterAccountCommand("pilot", "pilot@test", "pw"), CancellationToken.None);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await queries.ExecuteAsync(new ValidateCredentialsQuery("pilot"), CancellationToken.None), Is.True);
            Assert.That(await queries.ExecuteAsync(new ValidateCredentialsQuery("  pilot "), CancellationToken.None), Is.True);
            Assert.That(await queries.ExecuteAsync(new ValidateCredentialsQuery("PILOT"), CancellationToken.None), Is.False);
            Assert.That(await queries.ExecuteAsync(new ValidateCredentialsQuery(""), CancellationToken.None), Is.False);
            Assert.That(await queries.ExecuteAsync(new ValidateCredentialsQuery("pilot@test"), CancellationToken.None), Is.False);
            Assert.That(await queries.ExecuteAsync(new ValidateCredentialsQuery("unknown"), CancellationToken.None), Is.False);

            var summary = await queries.ExecuteAsync(new GetAccountByUsernameQuery(" pilot "), CancellationToken.None);
            Assert.That(summary, Is.Not.Null);
            Assert.That(summary!.Username, Is.EqualTo("pilot"));
            Assert.That(summary.Email, Is.EqualTo("pilot@test"));
            Assert.That(summary.GameUserId, Is.EqualTo(100_000_000));
            Assert.That(summary.Company, Is.Null);
            Assert.That(await queries.ExecuteAsync(new GetAccountByUsernameQuery("missing"), CancellationToken.None), Is.Null);
        });
    }

    // ---- §4 Company catalog ----

    [Test]
    public void CompanyCatalog_DefinesExactlyTheFourCompaniesInOrder()
    {
        var expected = new[]
        {
            new CompanyDefinition("mmo", "Mars Mining Operations TEST", 1, "1"),
            new CompanyDefinition("eic", "Earth Industries Corporation TEST", 2, "5"),
            new CompanyDefinition("vru", "Venus Resources Unlimited TEST", 3, "9"),
            new CompanyDefinition("tst", "Test Company TEST", 4, "13")
        };
        Assert.That(CompanyCatalog.Companies, Is.EqualTo(expected));
    }

    [Test]
    public void CompanyCatalog_CodesFactionIdsAndStarterMapsAreUnique()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CompanyCatalog.Companies.Select(c => c.Code), Is.Unique);
            Assert.That(CompanyCatalog.Companies.Select(c => c.NumericFactionId), Is.Unique);
            Assert.That(CompanyCatalog.Companies.Select(c => c.StarterMap), Is.Unique);
            Assert.That(CompanyCatalog.Companies.Select(c => c.Code), Has.All.Matches<string>(code => code.Length == 3 && code == code.ToLowerInvariant()));
            Assert.That(CompanyCatalog.Companies.Select(c => c.DisplayName), Has.All.EndsWith(" TEST"));
        });
    }

    [Test]
    public void CompanyCatalog_StarterMapIsTheFirstMapOfEachFactionBlock()
    {
        foreach (var company in CompanyCatalog.Companies)
            Assert.That(company.StarterMap, Is.EqualTo(((company.NumericFactionId - 1) * 4 + 1).ToString()), company.Code);
    }

    [TestCase("mmo")]
    [TestCase("eic")]
    [TestCase("vru")]
    [TestCase("tst")]
    public void CompanyCatalog_FindsEveryCanonicalCode(string code)
    {
        Assert.That(CompanyCatalog.Find(code)!.Code, Is.EqualTo(code));
        Assert.That(LocalCompanySelection.IsValid(code), Is.True);
        Assert.That(LocalCompanySelection.Faction(code), Is.EqualTo(code));
        Assert.That(LocalCompanySelection.StarterMap(code), Is.EqualTo(CompanyCatalog.Find(code)!.StarterMap));
        Assert.That(LocalCompanySelection.DisplayName(code), Is.EqualTo(CompanyCatalog.Find(code)!.DisplayName));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" ")]
    [TestCase("MMO")]
    [TestCase("Eic")]
    [TestCase(" mmo")]
    [TestCase("mmo ")]
    [TestCase("ru")]
    [TestCase("vr")]
    [TestCase("1")]
    [TestCase("mars")]
    [TestCase("xyz")]
    public void CompanyCatalog_RejectsNonCanonicalCodes(string? code)
    {
        Assert.Multiple(() =>
        {
            Assert.That(CompanyCatalog.Find(code), Is.Null);
            Assert.That(LocalCompanySelection.IsValid(code), Is.False);
            Assert.That(LocalCompanySelection.Faction(code), Is.Null);
            Assert.That(LocalCompanySelection.StarterMap(code), Is.Null);
            Assert.That(LocalCompanySelection.DisplayName(code), Is.Null);
        });
    }

    [Test]
    public void CompanyMigrations_CheckConstraintMatchesTheCatalog()
    {
        var sql = ReadMigration("005_add_test_company.up.pgsql");
        var expected = "CHECK (company IN (" + string.Join(", ", CompanyCatalog.Companies.Select(c => $"'{c.Code}'")) + "))";
        Assert.That(sql, Does.Contain(expected));
        Assert.That(ReadMigration("004_add_company.up.pgsql"), Does.Contain("ADD COLUMN company VARCHAR(3) NULL"));
    }

    // ---- §5 Company choice / confirmation ----

    [Test]
    public async Task SetCompany_FirstValidChoiceWinsAndIsIdempotent()
    {
        var (write, _, generator) = CreateServices();
        var commands = new IdentityCommandService(write, generator);
        var id = await RegisterAsync(write, commands, "pilot");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await commands.SetCompanyAsync(id, "eic", CancellationToken.None), Is.True);
            Assert.That(await commands.SetCompanyAsync(id, "eic", CancellationToken.None), Is.True, "Retrying the same choice is harmless.");
            Assert.That(await commands.SetCompanyAsync(id, "mmo", CancellationToken.None), Is.False, "A stored company cannot be switched.");
            Assert.That(await commands.SetCompanyAsync(id, "tst", CancellationToken.None), Is.False);
        });
        Assert.That((await write.Accounts.AsNoTracking().SingleAsync()).Company, Is.EqualTo("eic"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("MMO")]
    [TestCase("xyz")]
    public async Task SetCompany_InvalidCodeIsRejectedAndNothingIsStored(string? code)
    {
        var (write, _, generator) = CreateServices();
        var commands = new IdentityCommandService(write, generator);
        var id = await RegisterAsync(write, commands, "pilot");

        Assert.That(await commands.SetCompanyAsync(id, code!, CancellationToken.None), Is.False);
        Assert.That((await write.Accounts.AsNoTracking().SingleAsync()).Company, Is.Null);
    }

    [Test]
    public async Task SetCompany_UnknownAccountIsRejected()
    {
        var (write, _, generator) = CreateServices();
        var commands = new IdentityCommandService(write, generator);

        Assert.That(await commands.SetCompanyAsync(Guid.NewGuid(), "mmo", CancellationToken.None), Is.False);
    }

    [Test]
    public async Task SetCompany_LegacyNonCatalogValueCanBeReplacedByAValidChoice()
    {
        var (write, _, generator) = CreateServices();
        var commands = new IdentityCommandService(write, generator);
        var id = await RegisterAsync(write, commands, "pilot");
        var account = await write.Accounts.SingleAsync();
        account.Company = "old";
        await write.SaveChangesAsync();

        Assert.That(await commands.SetCompanyAsync(id, "vru", CancellationToken.None), Is.True);
        Assert.That((await write.Accounts.AsNoTracking().SingleAsync()).Company, Is.EqualTo("vru"));
    }

    [TestCase("mmo")]
    [TestCase("eic")]
    [TestCase("vru")]
    [TestCase("tst")]
    public async Task SetCompany_AfterFirstChoice_RejectsEveryOtherCatalogCompany(string firstChoice)
    {
        var (write, _, generator) = CreateServices();
        var commands = new IdentityCommandService(write, generator);
        var id = await RegisterAsync(write, commands, "pilot");

        Assert.That(await commands.SetCompanyAsync(id, firstChoice, CancellationToken.None), Is.True);
        foreach (var other in CompanyCodes.Where(code => code != firstChoice))
            Assert.That(await commands.SetCompanyAsync(id, other, CancellationToken.None), Is.False, other);

        Assert.That(await commands.SetCompanyAsync(id, firstChoice, CancellationToken.None), Is.True);
        Assert.That((await write.Accounts.AsNoTracking().SingleAsync()).Company, Is.EqualTo(firstChoice));
    }

    [Test]
    public async Task SetCompany_LegacyReplacement_ThenRejectsASecondDifferentChoice()
    {
        var (write, _, generator) = CreateServices();
        var commands = new IdentityCommandService(write, generator);
        var id = await RegisterAsync(write, commands, "pilot");
        var account = await write.Accounts.SingleAsync();
        account.Company = "old";
        await write.SaveChangesAsync();

        Assert.That(await commands.SetCompanyAsync(id, "vru", CancellationToken.None), Is.True);
        Assert.That(await commands.SetCompanyAsync(id, "mmo", CancellationToken.None), Is.False);
        Assert.That(await commands.SetCompanyAsync(id, "vru", CancellationToken.None), Is.True);
        Assert.That((await write.Accounts.AsNoTracking().SingleAsync()).Company, Is.EqualTo("vru"));
    }

    [Test]
    public void ChoicePage_RendersOneConfirmFormPerCompanyAndNoPreviewByDefault()
    {
        var page = LocalCompanySelection.ChoicePage;

        foreach (var company in CompanyCatalog.Companies)
        {
            Assert.That(page, Does.Contain($"data-company=\"{company.Code}\""));
            Assert.That(page, Does.Contain($"<input type=\"hidden\" name=\"company\" value=\"{company.Code}\">"));
            Assert.That(page, Does.Contain($"id=\"confirm_{company.Code}\""));
            Assert.That(page, Does.Contain($"Choose {company.Code.ToUpperInvariant()}"));
            Assert.That(page, Does.Contain(LocalCompanySelection.PreviewHref(company.Code)));
        }
        Assert.That(CountOccurrences(page, "value=\"internalCompanyConfirm\""), Is.EqualTo(CompanyCatalog.Companies.Count));
        Assert.That(page, Does.Not.Contain("id=\"companyPreview\""));
        Assert.That(page, Does.Not.Contain("aria-current"));
        Assert.That(page, Does.Contain("<title>Choose a company</title>"));
    }

    [TestCase("mmo")]
    [TestCase("eic")]
    [TestCase("vru")]
    [TestCase("tst")]
    public void ChoicePage_PreviewHighlightsExactlyOneCompany(string code)
    {
        var page = LocalCompanySelection.ChoicePageFor(code);

        Assert.That(page, Does.Contain($"<section id=\"companyPreview\" data-company=\"{code}\">"));
        Assert.That(CountOccurrences(page, "aria-current=\"true\""), Is.EqualTo(1));
        Assert.That(page, Does.Contain($"data-company=\"{code}\" aria-current=\"true\""));
        Assert.That(page, Does.Contain("id=\"previewConfirm\""));
        Assert.That(page, Does.Contain("Previewing does not save anything."));
    }

    [Test]
    public void ChoicePage_UnknownPreviewFallsBackToThePlainChoice()
        => Assert.That(LocalCompanySelection.ChoicePageFor("xyz"), Is.EqualTo(LocalCompanySelection.ChoicePage));

    [TestCase("mmo")]
    [TestCase("eic")]
    [TestCase("vru")]
    [TestCase("tst")]
    public void ChoicePage_PreviewFactsUseCatalogMapAndMapServer(string code)
    {
        var company = CompanyCatalog.Find(code)!;
        var server = LocalMapsXml.Maps.Single(map => map.Id == company.StarterMap).GameServerIp;
        var page = LocalCompanySelection.ChoicePageFor(code);

        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain($"<dt>Starter map</dt><dd>{company.StarterMap}</dd>"));
            Assert.That(page, Does.Contain($"<dt>Game server</dt><dd><code>{server}</code></dd>"));
            Assert.That(page, Does.Contain("<dt>Shares starter map with</dt><dd>none</dd>"));
        });
    }

    [Test]
    public void TestCompany_PreviewAndConfirmationDoNotClaimAnotherCompanyMap()
    {
        var testCompany = CompanyCatalog.Find("tst")!;
        var otherMaps = CompanyCatalog.Companies.Where(company => company.Code != "tst")
            .Select(company => company.StarterMap).ToArray();
        var preview = LocalCompanySelection.ChoicePageFor("tst");
        var confirmation = LocalCompanySelection.ConfirmationPage("tst");

        Assert.That(otherMaps, Does.Not.Contain(testCompany.StarterMap));
        Assert.Multiple(() =>
        {
            Assert.That(preview, Does.Contain("It uses its own starter map."));
            Assert.That(confirmation, Does.Contain($"id=\"companyTestNotice\">TST is the local test company. Its starter map is {testCompany.StarterMap}."));
            Assert.That(preview, Does.Not.Contain("shares the MMO starter map"));
            Assert.That(confirmation, Does.Not.Contain("shares the MMO starter map"));
        });
    }

    [TestCase("mmo")]
    [TestCase("eic")]
    [TestCase("vru")]
    [TestCase("tst")]
    public void ConfirmationPage_DerivesEveryFactFromTheCatalog(string code)
    {
        var company = CompanyCatalog.Find(code)!;
        var page = LocalCompanySelection.ConfirmationPage(code);

        Assert.Multiple(() =>
        {
            Assert.That(page, Does.Contain($"id=\"companyConfirmation\" data-company=\"{code}\""));
            Assert.That(page, Does.Contain(company.DisplayName));
            Assert.That(page, Does.Contain($"{company.NumericFactionId}-1 (map {company.StarterMap})"));
            Assert.That(page, Does.Contain($"map={company.StarterMap}"));
            Assert.That(page, Does.Contain($"<dt>Game server</dt><dd><code>{LocalMapsXml.Maps.Single(map => map.Id == company.StarterMap).GameServerIp}</code></dd>"));
            Assert.That(page, Does.Contain("id=\"continueToGame\" href=\"/indexInternal.es?action=internalWebGL&amp;from=internalStart\""));
            Assert.That(page.Contains("id=\"companyTestNotice\""), Is.EqualTo(code == "tst"));
        });
    }

    [Test]
    public void GamePage_PilotCardEncodesNameAndHidesNonPositiveIds()
    {
        var page = LocalCompanySelection.GamePage("mmo", "<b>pilot</b>", 100_000_000);
        Assert.That(page, Does.Not.Contain("<b>pilot</b>"));
        Assert.That(page, Does.Contain("&lt;b&gt;pilot&lt;/b&gt;"));
        Assert.That(page, Does.Contain("<dd id=\"pilotGameId\">100000000</dd>"));
        Assert.That(page, Does.Contain("<dd id=\"pilotCompany\">MMO</dd>"));

        Assert.That(LocalCompanySelection.GamePage("eic", null, 0), Does.Not.Contain("pilotGameId"));
        Assert.That(LocalCompanySelection.GamePage("eic", "  ", null), Does.Contain("<h2 id=\"pilotName\">Pilot</h2>"));
    }

    // ---- §6 Starter provisioning ----

    [Test]
    public async Task StarterProvisioning_NewAccountGetsTheStarterInventoryAndWallet()
    {
        var store = new LocalHangarEquipmentStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));

        var state = await store.GetAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(state.Credits, Is.EqualTo(10_000));
            Assert.That(state.AssemblyComponents, Is.EqualTo(1_000));
            Assert.That(state.Assignments, Is.Empty);
            Assert.That(state.Inventory.Select(item => (item.Id, item.Quantity, item.Type)), Is.EqualTo(new (string, int, string?)[]
            {
                ("laser_lf3", 4, "laser"),
                ("laser_lf2", 2, "laser"),
                ("shield_sg3n_b02", 4, "shield"),
                ("shield_sg3n_a03", 2, "shield"),
                ("drone_flax", 1, "drone"),
                ("drone_iris", 1, "drone"),
                ("pet_10", 1, "pet"),
                ("extra_cpu", 0, "extra")
            }));
        });
    }

    [Test]
    public async Task StarterProvisioning_IsPerAccountAndNotSharedByReference()
    {
        var store = new LocalHangarEquipmentStore(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));

        var first = await store.GetAsync(Guid.NewGuid(), CancellationToken.None);
        first.Inventory.Clear();
        first.Credits = 1;
        var second = await store.GetAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.That(second.Inventory, Has.Count.EqualTo(8));
        Assert.That(second.Credits, Is.EqualTo(10_000));
    }

    // ---- §7 Keycloak RBAC ----

    [Test]
    public void Rbac_DefinesTheThreeRealmRolesAndSevenPermissions()
    {
        Assert.That(KeycloakRbacDefinitions.ApiClientId, Is.EqualTo("thdarkorbit-api"));
        Assert.That(KeycloakRbacDefinitions.RealmRolePermissions.Keys,
            Is.EquivalentTo(new[] { "emulator-admin", "emulator-operator", "emulator-viewer" }));
        Assert.That(KeycloakRbacDefinitions.AllPermissions, Is.EqualTo(new[]
        {
            "backoffice.identity.read", "backoffice.identity.write", "backoffice.identity.delete",
            "backoffice.keycloak.read", "backoffice.cache.write", "backoffice.ops.read", "backoffice.ops.write"
        }));
        Assert.That(KeycloakRbacDefinitions.AllPermissions, Is.Unique);
        Assert.That(KeycloakRbacDefinitions.AllPermissions, Has.All.StartsWith("backoffice."));
    }

    // Matrix rows: permission -> admin, operator, viewer.
    [TestCase("backoffice.identity.read", true, true, true)]
    [TestCase("backoffice.identity.write", true, true, false)]
    [TestCase("backoffice.identity.delete", true, false, false)]
    [TestCase("backoffice.keycloak.read", true, true, true)]
    [TestCase("backoffice.cache.write", true, true, false)]
    [TestCase("backoffice.ops.read", true, true, true)]
    [TestCase("backoffice.ops.write", true, true, false)]
    public void Rbac_PermissionMatrixIsExact(string permission, bool admin, bool @operator, bool viewer)
    {
        var map = KeycloakRbacDefinitions.RealmRolePermissions;
        Assert.Multiple(() =>
        {
            Assert.That(map[KeycloakRbacDefinitions.AdminRole].Contains(permission), Is.EqualTo(admin));
            Assert.That(map[KeycloakRbacDefinitions.OperatorRole].Contains(permission), Is.EqualTo(@operator));
            Assert.That(map[KeycloakRbacDefinitions.ViewerRole].Contains(permission), Is.EqualTo(viewer));
        });
    }

    [Test]
    public void Rbac_RolesAreStrictlyNestedAndOnlyGrantKnownPermissions()
    {
        var map = KeycloakRbacDefinitions.RealmRolePermissions;
        var admin = map[KeycloakRbacDefinitions.AdminRole];
        var @operator = map[KeycloakRbacDefinitions.OperatorRole];
        var viewer = map[KeycloakRbacDefinitions.ViewerRole];

        Assert.Multiple(() =>
        {
            Assert.That(admin, Is.EquivalentTo(KeycloakRbacDefinitions.AllPermissions));
            Assert.That(viewer, Is.SubsetOf(@operator));
            Assert.That(@operator, Is.SubsetOf(admin));
            Assert.That(viewer.Count, Is.LessThan(@operator.Count));
            Assert.That(@operator.Count, Is.LessThan(admin.Count));
            foreach (var permissions in map.Values)
            {
                Assert.That(permissions, Is.Unique);
                Assert.That(permissions, Is.SubsetOf(KeycloakRbacDefinitions.AllPermissions));
            }
            Assert.That(viewer.Where(p => !p.EndsWith(".read", StringComparison.Ordinal)), Is.Empty, "Viewer is read-only.");
            Assert.That(@operator, Does.Not.Contain(KeycloakRbacDefinitions.IdentityDelete), "Only admin may delete identities.");
        });
    }

    [Test]
    public void Rbac_DefaultRoleForNewPlayersIsTheLeastPrivilegedRole()
    {
        var map = KeycloakRbacDefinitions.RealmRolePermissions;
        Assert.That(KeycloakRbacDefinitions.DefaultRealmRole, Is.EqualTo(KeycloakRbacDefinitions.ViewerRole));
        Assert.That(map[KeycloakRbacDefinitions.DefaultRealmRole].Count, Is.EqualTo(map.Values.Min(p => p.Count)));
    }

    // ---- helpers ----

    private sealed class SequenceGenerator : IGameUserIdGenerator
    {
        private long _next = 100_000_000;
        public int Calls { get; private set; }

        public Task<long> NextAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_next++);
        }
    }

    private static IdentityWriteDbContext CreateWriteContext(string name)
        => new(new DbContextOptionsBuilder<IdentityWriteDbContext>().UseInMemoryDatabase(name).Options,
            Options.Create(new IdentityDatabaseOptions { Schema = "identity" }));

    private static (IdentityWriteDbContext Write, IdentityReadDbContext Read, SequenceGenerator Generator) CreateServices()
    {
        var name = Guid.NewGuid().ToString();
        var options = Options.Create(new IdentityDatabaseOptions { Schema = "identity" });
        var read = new IdentityReadDbContext(new DbContextOptionsBuilder<IdentityReadDbContext>().UseInMemoryDatabase(name).Options, options);
        return (CreateWriteContext(name), read, new SequenceGenerator());
    }

    private static async Task<Guid> RegisterAsync(IdentityWriteDbContext write, IdentityCommandService commands, string username)
    {
        await commands.ExecuteAsync(new RegisterAccountCommand(username, null, "pw"), CancellationToken.None);
        return (await write.Accounts.AsNoTracking().SingleAsync(a => a.Username == username)).Id;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string ReadMigration(string file)
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ThDarkOrbitEmulator.slnx")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, "ThDarkOrbitEmulator.Identity.Infrastructure", "Migrations", "Sql", file));
    }
}
