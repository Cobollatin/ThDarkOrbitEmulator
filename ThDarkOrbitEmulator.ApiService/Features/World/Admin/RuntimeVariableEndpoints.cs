using FastEndpoints;
using Microsoft.Extensions.Options;
using ThDarkOrbitEmulator.Cluster.Infrastructure;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.ApiService.Features.World.Admin;

public sealed record ChangeRuntimeVariableRequest(string Value, string Reason);
public sealed record ExplainRuntimeVariableChangeRequest(string Reason);

internal static class RuntimeVariableAttribution
{
    public static RuntimeVariableActor Actor(HttpContext context, ClusterOptions cluster, string reason) =>
        new(context.Request.Headers["X-Admin-Surface"] == "ServerManagement" ? "ServerManagement" : "AdminCms",
            context.User.FindFirst("sub")?.Value ?? context.User.Identity?.Name ?? "unknown",
            string.IsNullOrWhiteSpace(cluster.NodeId) ? $"{Environment.MachineName}-{Environment.ProcessId}" : cluster.NodeId,
            reason);
}

public sealed class ListRuntimeVariablesEndpoint(IRuntimeVariableRegistry registry)
    : EndpointWithoutRequest<IReadOnlyList<RuntimeVariable>>
{
    public override void Configure()
    {
        Get("backoffice/runtime-variables", "backoffice/runtime-vars"); Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsRead));
        Description(d => d.WithTags("Backoffice"));
    }
    public override async Task HandleAsync(CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.OkAsync(await registry.ListAsync(ct), ct);
    }
}

public sealed class RuntimeVariableHistoryEndpoint(IRuntimeVariableRegistry registry)
    : EndpointWithoutRequest<IReadOnlyList<RuntimeVariableChangeEntry>>
{
    public override void Configure()
    {
        Get("backoffice/runtime-variables/history", "backoffice/runtime-vars/history"); Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsRead));
        Description(d => d.WithTags("Backoffice"));
    }
    public override async Task HandleAsync(CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        await Send.OkAsync(await registry.HistoryAsync(HttpContext.Request.Query["key"].FirstOrDefault(),
            int.TryParse(HttpContext.Request.Query["limit"], out var limit) ? limit : 100,
            long.TryParse(HttpContext.Request.Query["beforeChangeId"], out var before) ? before : null, ct), ct);
    }
}

public sealed class GetRuntimeVariableEndpoint(IRuntimeVariableRegistry registry)
    : EndpointWithoutRequest<RuntimeVariable>
{
    public override void Configure()
    {
        Get("backoffice/runtime-variables/{key}", "backoffice/runtime-vars/{key}"); Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsRead));
        Description(d => d.WithTags("Backoffice"));
    }
    public override async Task HandleAsync(CancellationToken ct)
    {
        try
        {
            HttpContext.Response.Headers.CacheControl = "no-store";
            await Send.OkAsync(await registry.GetAsync(Route<string>("key")!, ct), ct);
        }
        catch (KeyNotFoundException) { await Send.NotFoundAsync(ct); }
    }
}

public sealed class SetRuntimeVariableEndpoint(IRuntimeVariableRegistry registry,
    IOptions<ClusterOptions> cluster) : Endpoint<ChangeRuntimeVariableRequest, RuntimeVariableChangeEntry>
{
    public override void Configure()
    {
        Put("backoffice/runtime-variables/{key}", "backoffice/runtime-vars/{key}"); Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsWrite));
        Description(d => d.WithTags("Backoffice"));
    }
    public override async Task HandleAsync(ChangeRuntimeVariableRequest req, CancellationToken ct)
    {
        try
        {
            HttpContext.Response.Headers.CacheControl = "no-store";
            await Send.OkAsync(await registry.SetAsync(Route<string>("key")!, req.Value,
                RuntimeVariableAttribution.Actor(HttpContext, cluster.Value, req.Reason), ct), ct);
        }
        catch (ArgumentException error)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(new { error = error.Message }, ct);
        }
    }
}

public sealed class ResetRuntimeVariableEndpoint(IRuntimeVariableRegistry registry,
    IOptions<ClusterOptions> cluster) : Endpoint<ExplainRuntimeVariableChangeRequest, RuntimeVariableChangeEntry>
{
    public override void Configure()
    {
        Post("backoffice/runtime-variables/{key}/reset", "backoffice/runtime-vars/{key}/reset"); Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsWrite));
        Description(d => d.WithTags("Backoffice"));
    }
    public override async Task HandleAsync(ExplainRuntimeVariableChangeRequest req, CancellationToken ct)
    {
        try { await Send.OkAsync(await registry.ResetToDefaultAsync(Route<string>("key")!,
            RuntimeVariableAttribution.Actor(HttpContext, cluster.Value, req.Reason), ct), ct); }
        catch (KeyNotFoundException) { await Send.NotFoundAsync(ct); }
        catch (ArgumentException error)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(new { error = error.Message }, ct);
        }
    }
}

public sealed class RollbackRuntimeVariableEndpoint(IRuntimeVariableRegistry registry,
    IOptions<ClusterOptions> cluster) : Endpoint<ExplainRuntimeVariableChangeRequest, RuntimeVariableChangeEntry>
{
    public override void Configure()
    {
        Post("backoffice/runtime-variables/history/{changeId}/rollback", "backoffice/runtime-vars/history/{changeId}/rollback"); Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsWrite));
        Description(d => d.WithTags("Backoffice"));
    }
    public override async Task HandleAsync(ExplainRuntimeVariableChangeRequest req, CancellationToken ct)
    {
        if (!long.TryParse(Route<string>("changeId"), out var id)) { await Send.NotFoundAsync(ct); return; }
        try { await Send.OkAsync(await registry.RollbackAsync(id,
            RuntimeVariableAttribution.Actor(HttpContext, cluster.Value, req.Reason), ct), ct); }
        catch (KeyNotFoundException) { await Send.NotFoundAsync(ct); }
        catch (InvalidOperationException error)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status409Conflict;
            await HttpContext.Response.WriteAsJsonAsync(new { error = error.Message }, ct);
        }
        catch (ArgumentException error)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(new { error = error.Message }, ct);
        }
    }
}
