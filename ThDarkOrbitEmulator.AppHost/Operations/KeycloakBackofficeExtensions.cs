using System.Net.Http.Headers;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace ThDarkOrbitEmulator.AppHost.Operations;

internal static class KeycloakBackofficeExtensions
{
    private const string Realm = "thdarkorbit";
    private const string AdminUser = "admin";
    private const string AdminPassword = "admin";

    public static IResourceBuilder<ContainerResource> WithKeycloakBackofficeCommands(
        this IResourceBuilder<ContainerResource> builder)
    {
        builder.WithCommand(
            "bo-keycloak-users",
            "BO: List Keycloak users",
            ListKeycloakUsersAsync,
            new CommandOptions
            {
                Description = "Lists users in realm thdarkorbit via Admin REST API.",
                IconName = "Person",
            });

        return builder;
    }

    private static async Task<ExecuteCommandResult> ListKeycloakUsersAsync(ExecuteCommandContext context)
    {
        try
        {
            // AppHost commands run on the dev machine; Keycloak is published on host port 8081.
            const string baseUrl = "http://localhost:8081";
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
            var token = await RequestAdminTokenAsync(http, context.CancellationToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                return CommandResults.Failure("Could not obtain Keycloak admin token (is Keycloak still starting?).");
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"/admin/realms/{Realm}/users?max=50");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await http.SendAsync(request, context.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(context.CancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return CommandResults.Failure(
                    $"Keycloak Admin API returned {(int)response.StatusCode}.",
                    body,
                    CommandResultFormat.Text);
            }

            var rows = ParseUsers(body);
            var markdown = MarkdownReportBuilder.Build($"Keycloak users (realm `{Realm}`)", sb =>
            {
                sb.AppendLine($"Admin API: `{baseUrl}`");
                sb.AppendLine();
                if (rows.Count == 0)
                {
                    sb.AppendLine("*No users in realm.*");
                }
                else
                {
                    MarkdownReportBuilder.WriteTable(
                        sb,
                        ["Id", "Username", "Email", "Enabled"],
                        rows);
                }

                sb.AppendLine("Identity table `identity.accounts` is separate — use **BO: List identity users** on database `thdarkorbit`.");
            });

            return OperationsCommandResults.MarkdownReport("Keycloak user list generated.", markdown);
        }
        catch (Exception ex)
        {
            return CommandResults.Failure($"Failed to list Keycloak users: {ex.Message}");
        }
    }

    private static List<IReadOnlyList<string>> ParseUsers(string json)
    {
        var rows = new List<IReadOnlyList<string>>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var user in document.RootElement.EnumerateArray())
        {
            var id = user.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            var username = user.TryGetProperty("username", out var uEl) ? uEl.GetString() ?? "" : "";
            var email = user.TryGetProperty("email", out var eEl) ? eEl.GetString() ?? "" : "";
            var enabled = user.TryGetProperty("enabled", out var enEl) && enEl.ValueKind == JsonValueKind.True
                ? "yes"
                : "no";
            rows.Add([id, username, email, enabled]);
        }

        return rows;
    }

    private static async Task<string?> RequestAdminTokenAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = AdminUser,
            ["password"] = AdminPassword
        };

        using var response = await http.PostAsync(
            "/realms/master/protocol/openid-connect/token",
            new FormUrlEncodedContent(form),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("access_token", out var token)
            ? token.GetString()
            : null;
    }
}
