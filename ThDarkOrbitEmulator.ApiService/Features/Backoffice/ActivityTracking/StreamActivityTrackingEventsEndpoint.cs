using System.Text.Json;
using FastEndpoints;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.ApiService.Features.Backoffice.ActivityTracking;

public sealed class StreamActivityTrackingEventsEndpoint(IManualActivityTracker tracker) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("backoffice/activity-tracking/sessions/{sessionId}/events/stream");
        Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsRead));
        Description(d => d.WithTags("Backoffice"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var sessionId = Route<string>("sessionId") ?? "";
        if (!tracker.TryGetSession(sessionId, out _))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        HttpContext.Response.ContentType = "text/event-stream";
        HttpContext.Response.Headers.CacheControl = "no-store";

        await foreach (var activityEvent in tracker.SubscribeAsync(sessionId, ct))
        {
            await HttpContext.Response.WriteAsync($"id: {activityEvent.Sequence}\n", ct);
            await HttpContext.Response.WriteAsync("event: activity\n", ct);
            await HttpContext.Response.WriteAsync($"data: {JsonSerializer.Serialize(activityEvent)}\n\n", ct);
            await HttpContext.Response.Body.FlushAsync(ct);
        }
    }
}
