using FastEndpoints;
using Microsoft.Extensions.Options;
using Npgsql;
using ThDarkOrbitEmulator.Cluster.Infrastructure;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.ApiService.Features.World.Admin;

public sealed record ExecuteAdminCommandRequest(string? Text, string? Verb, string[]? Arguments,
    string Surface = "AdminCms");

public sealed class AdminCommandEndpoint(IAdminCommandDispatcher dispatcher, IOptions<ClusterOptions> cluster)
    : Endpoint<ExecuteAdminCommandRequest, AdminCommandResult>
{
    public override void Configure()
    {
        Post("backoffice/admin/command"); Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsWrite));
        Description(d => d.WithTags("Backoffice"));
    }

    public override async Task HandleAsync(ExecuteAdminCommandRequest req, CancellationToken ct)
    {
        if (req.Surface is not ("ServerManagement" or "AdminCms" or "InGameAdmin"))
        {
            await ErrorAsync(400, new ArgumentException("Invalid admin surface."), ct);
            return;
        }
        try
        {
            HttpContext.Response.Headers.CacheControl = "no-store";
            await Send.OkAsync(await dispatcher.ExecuteAsync(
                new AdminCommand(req.Text, req.Verb, req.Arguments),
                new AdminCommandActor(req.Surface,
                    HttpContext.User.FindFirst("sub")?.Value ?? HttpContext.User.Identity?.Name ?? "unknown",
                    string.IsNullOrWhiteSpace(cluster.Value.NodeId)
                        ? $"{Environment.MachineName}-{Environment.ProcessId}" : cluster.Value.NodeId), ct), ct);
        }
        catch (KeyNotFoundException error) { await ErrorAsync(404, error, ct); }
        catch (NotSupportedException error) { await ErrorAsync(501, error, ct); }
        catch (InvalidOperationException error) { await ErrorAsync(409, error, ct); }
        catch (ArgumentException error) { await ErrorAsync(400, error, ct); }
    }

    private async Task ErrorAsync(int status, Exception error, CancellationToken ct)
    {
        HttpContext.Response.StatusCode = status;
        await HttpContext.Response.WriteAsJsonAsync(new { error = error.Message }, ct);
    }
}

public sealed record AdminCommandHistoryEntry(long CommandId, string Surface, string Actor,
    string NodeId, string CommandText, bool Succeeded, string? Error, DateTimeOffset ExecutedAtUtc);

public sealed class AdminCommandHistoryEndpoint(IConfiguration configuration)
    : EndpointWithoutRequest<IReadOnlyList<AdminCommandHistoryEntry>>
{
    public override void Configure()
    {
        Get("backoffice/admin/command/history"); Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsRead));
        Description(d => d.WithTags("Backoffice"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var limit = int.TryParse(HttpContext.Request.Query["limit"], out var requested)
            ? Math.Clamp(requested, 1, 1000) : 100;
        await using var db = new NpgsqlConnection(configuration.GetConnectionString("identity-write")
            ?? throw new InvalidOperationException("identity-write connection is required."));
        await db.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT command_id,surface,actor,node_id,command_text,succeeded,error,executed_at_utc FROM identity.admin_command_history ORDER BY command_id DESC LIMIT @limit", db);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<AdminCommandHistoryEntry>();
        while (await reader.ReadAsync(ct)) rows.Add(new(reader.GetInt64(0), reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetFieldValue<DateTimeOffset>(7)));
        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.OkAsync(rows, ct);
    }
}
