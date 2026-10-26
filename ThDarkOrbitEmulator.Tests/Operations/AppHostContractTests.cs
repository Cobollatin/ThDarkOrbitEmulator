using System.Text.RegularExpressions;
using ThDarkOrbitEmulator.AppHost.Dashboard;

namespace ThDarkOrbitEmulator.Tests.Operations;

public class AppHostContractTests
{
    private static readonly Regex KebabCase = new("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.Compiled);

    [Test]
    public void DashboardResourceNames_AreUniquePurposeFirstKebabCaseNames()
    {
        var values = typeof(DashboardResourceNames)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string))
            .Select(field => (Field: field.Name, Value: (string)field.GetValue(null)!))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(values, Has.Length.EqualTo(11));
            Assert.That(values.Select(value => value.Value).Distinct(StringComparer.Ordinal).ToArray(), Has.Length.EqualTo(values.Length));
            Assert.That(values, Has.All.Matches<(string Field, string Value)>(value => KebabCase.IsMatch(value.Value)));
            Assert.That(values.Select(value => value.Value), Does.Not.Contain("redis-sessions"));
            Assert.That(values.Select(value => value.Value), Does.Not.Contain("postgres-data"));
        });
    }

    [Test]
    public void AppHost_KeycloakDependentProjectsUseEndpointExpressions()
    {
        var appHostSource = File.ReadAllText(FindAppHostSource());

        Assert.Multiple(() =>
        {
            Assert.That(appHostSource, Does.Contain("var keycloakBaseUrl = authKeycloak.GetEndpoint(\"http\");"));
            Assert.That(appHostSource, Does.Contain("ReferenceExpression.Create($\"{keycloakBaseUrl}/realms/thdarkorbit\")"));
            Assert.That(appHostSource, Does.Not.Contain(".WithEnvironment(\"Keycloak__Authority\", \"http://localhost:8081/realms/thdarkorbit\")"));
            Assert.That(appHostSource, Does.Not.Contain(".WithEnvironment(\"KeycloakAdmin__BaseUrl\", \"http://localhost:8081\")"));
        });
    }

    private static string FindAppHostSource()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "ThDarkOrbitEmulator.AppHost", "AppHost.cs");
            if (File.Exists(path))
            {
                return path;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate AppHost.cs.");
    }
}
