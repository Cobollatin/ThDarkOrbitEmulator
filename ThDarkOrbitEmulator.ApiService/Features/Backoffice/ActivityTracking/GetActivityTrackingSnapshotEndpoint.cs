using FastEndpoints;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.ApiService.Features.Backoffice.ActivityTracking;

public sealed class GetActivityTrackingSnapshotEndpoint(IManualActivityTracker tracker)
    : EndpointWithoutRequest<ActivityTrackingSnapshotResponse>
{
    public override void Configure()
    {
        Get("backoffice/activity-tracking/sessions/{sessionId}");
        Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsRead));
        Description(d => d.WithTags("Backoffice"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var sessionId = Route<string>("sessionId") ?? "";
        if (!tracker.TryGetSession(sessionId, out var session))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(new ActivityTrackingSnapshotResponse
        {
            Session = session,
            Events = tracker.GetSnapshot(sessionId)
        }, ct);
    }
}
