using System.Text.RegularExpressions;

namespace ThDarkOrbitEmulator.Identity.Infrastructure;

internal static partial class PostgresIdentifier
{
    public static string RequireSchema(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema) || !SchemaName().IsMatch(schema))
        {
            throw new InvalidOperationException($"Invalid Postgres schema name '{schema}'.");
        }

        return schema;
    }

    [GeneratedRegex("^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex SchemaName();
}
