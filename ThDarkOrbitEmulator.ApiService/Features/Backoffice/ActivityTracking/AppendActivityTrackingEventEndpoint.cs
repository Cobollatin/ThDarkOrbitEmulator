using FastEndpoints;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.ApiService.Features.Backoffice.ActivityTracking;

public sealed class AppendActivityTrackingEventEndpoint(IManualActivityTracker tracker)
    : Endpoint<AppendActivityTrackingEventRequest, AppendActivityTrackingEventResponse>
{
    public override void Configure()
    {
        Post("backoffice/activity-tracking/sessions/{sessionId}/events");
        Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsRead));
        Description(d => d.WithTags("Backoffice"));
    }

    public override async Task HandleAsync(AppendActivityTrackingEventRequest req, CancellationToken ct)
    {
        var sessionId = Route<string>("sessionId") ?? "";
        if (!tracker.TryGetSession(sessionId, out _))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var activityEvent = tracker.AppendEvent(
            sessionId,
            req.Type,
            req.Source,
            req.Details,
            req.OccurredAtUtc ?? DateTimeOffset.UtcNow);

        await Send.OkAsync(new AppendActivityTrackingEventResponse { Event = activityEvent }, ct);
    }
}
