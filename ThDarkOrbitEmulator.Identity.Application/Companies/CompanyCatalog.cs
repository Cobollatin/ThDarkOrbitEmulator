namespace ThDarkOrbitEmulator.Identity.Application;

public sealed record CompanyDefinition(string Code, string DisplayName, int NumericFactionId, string StarterMap);

public static class CompanyCatalog
{
    public static IReadOnlyList<CompanyDefinition> Companies { get; } =
    [
        new("mmo", "Mars Mining Operations TEST", 1, "1"),
        new("eic", "Earth Industries Corporation TEST", 2, "5"),
        new("vru", "Venus Resources Unlimited TEST", 3, "9"),
        new("tst", "Test Company TEST", 4, "13")
    ];

    public static CompanyDefinition? Find(string? code)
        => Companies.FirstOrDefault(company => company.Code == code);
}
