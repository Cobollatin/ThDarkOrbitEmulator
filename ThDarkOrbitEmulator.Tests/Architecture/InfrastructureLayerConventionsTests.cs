using NetArchTest.Rules;
using System.Text.RegularExpressions;
using ThDarkOrbitEmulator.Cluster.Infrastructure;
using ThDarkOrbitEmulator.Identity.Infrastructure;

namespace ThDarkOrbitEmulator.Tests.Architecture;

public class InfrastructureLayerConventionsTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Test]
    public void IdentityInfrastructure_DoesNotDependOnApiServiceOrCluster()
    {
        var result = Types
            .InAssembly(typeof(IdentityCommandService).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "FastEndpoints",
                "ThDarkOrbitEmulator.ApiService",
                "ThDarkOrbitEmulator.Cluster.Application",
                "ThDarkOrbitEmulator.Cluster.Infrastructure")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True);
    }

    [Test]
    public void CompanyChoice_UsesCatalogValidationAndAtomicRelationalUpdate()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot,
            "ThDarkOrbitEmulator.Identity.Infrastructure", "Accounts", "IdentityCommandService.cs"));
        var methodStart = source.IndexOf("public async Task<bool> SetCompanyAsync(", StringComparison.Ordinal);
        var registrationStart = source.IndexOf("public async Task<bool> ExecuteAsync(RegisterAccountCommand", StringComparison.Ordinal);
        Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(registrationStart, Is.GreaterThan(methodStart));
        var choice = source[methodStart..registrationStart];

        Assert.Multiple(() =>
        {
            Assert.That(choice, Does.Contain("CompanyCatalog.Find(company) is null"));
            Assert.That(choice, Does.Contain("dbContext.Database.IsRelational()"));
            Assert.That(choice, Does.Contain("a.Id == accountId && (a.Company == null || !codes.Contains(a.Company))"));
            Assert.That(choice, Does.Contain("ExecuteUpdateAsync(setters => setters.SetProperty(a => a.Company, company), cancellationToken)"));
            Assert.That(choice, Does.Contain("string.Equals(stored, company, StringComparison.Ordinal)"));
        });
    }

    [Test]
    public void ClusterInfrastructure_DoesNotDependOnApiServiceOrIdentity()
    {
        var result = Types
            .InAssembly(typeof(SshWorkerSpawner).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "FastEndpoints",
                "ThDarkOrbitEmulator.ApiService",
                "ThDarkOrbitEmulator.Identity.Domain",
                "ThDarkOrbitEmulator.Identity.Application",
                "ThDarkOrbitEmulator.Identity.Infrastructure")
            .GetResult();

        Assert.That(result.IsSuccessful, Is.True);
    }

    [Test]
    public void IdentityInfrastructure_IsOrganizedIntoScreamingSubfoldersAndMigrationsHavePairedUpDownScripts()
    {
        var infraDir = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.Identity.Infrastructure");
        var folders = Directory.GetDirectories(infraDir)
            .Select(Path.GetFileName)
            .Where(name => name is not ("bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.That(folders, Is.EqualTo(new[] { "Accounts", "Backoffice", "Keycloak", "Migrations", "Persistence", "Seeding" }));

        var migrationsDir = Path.Combine(infraDir, "Migrations");
        var emptySubDirs = Directory.GetDirectories(migrationsDir, "*", SearchOption.AllDirectories)
            .Where(d => Directory.GetFileSystemEntries(d).Length == 0)
            .ToArray();
        Assert.That(emptySubDirs, Is.Empty, "Migrations directory should not contain empty subdirectories.");

        var upFiles = Directory.GetFiles(migrationsDir, "*.up.pgsql", SearchOption.AllDirectories)
            .Select(path => Path.GetFileName(path)[..^".up.pgsql".Length])
            .Order(StringComparer.Ordinal)
            .ToArray();
        var downFiles = Directory.GetFiles(migrationsDir, "*.down.pgsql", SearchOption.AllDirectories)
            .Select(path => Path.GetFileName(path)[..^".down.pgsql".Length])
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(upFiles, Is.Not.Empty);
            Assert.That(upFiles, Is.EqualTo(downFiles), "Every .up.pgsql migration must have a matching .down.pgsql rollback script.");
        });
    }

    [Test]
    public void IdentitySqlMigrations_AreSequentialPairedAndNonEmpty()
    {
        var sqlDir = Path.Combine(RepositoryRoot, "ThDarkOrbitEmulator.Identity.Infrastructure", "Migrations", "Sql");
        var files = Directory.GetFiles(sqlDir, "*.pgsql", SearchOption.TopDirectoryOnly);
        var names = files.Select(Path.GetFileName).ToArray();
        Assert.That(names, Is.Not.Empty);
        Assert.That(names.Where(name => !Regex.IsMatch(name!, @"^\d{3}_[a-z0-9_]+\.(up|down)\.pgsql$")),
            Is.Empty, "Migration files must use a three-digit number, name, and up/down suffix.");

        var groups = files.GroupBy(path => Path.GetFileName(path)[..3])
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();
        Assert.That(groups.Select(group => group.Key),
            Is.EqualTo(Enumerable.Range(0, groups.Length).Select(index => index.ToString("D3"))),
            "Migration numbers must be contiguous from 000.");

        foreach (var group in groups)
        {
            Assert.That(group.Count(), Is.EqualTo(2), $"Migration {group.Key} needs exactly one up and one down script.");
            var up = group.Single(path => path.EndsWith(".up.pgsql", StringComparison.Ordinal));
            var down = group.Single(path => path.EndsWith(".down.pgsql", StringComparison.Ordinal));
            Assert.That(Path.GetFileName(up)[..^".up.pgsql".Length],
                Is.EqualTo(Path.GetFileName(down)[..^".down.pgsql".Length]),
                $"Migration {group.Key} up/down names must match.");
            Assert.That(File.ReadAllText(up), Is.Not.Empty.And.Not.WhiteSpace, up);
            Assert.That(File.ReadAllText(down), Is.Not.Empty.And.Not.WhiteSpace, down);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ThDarkOrbitEmulator.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
