using System.Security.Claims;
using FastEndpoints;
using ThDarkOrbitEmulator.ServiceDefaults.Authorization;

namespace ThDarkOrbitEmulator.ApiService.Features.Backoffice.ActivityTracking;

public sealed class StartActivityTrackingSessionEndpoint(IManualActivityTracker tracker)
    : Endpoint<StartActivityTrackingSessionRequest, StartActivityTrackingSessionResponse>
{
    public override void Configure()
    {
        Post("backoffice/activity-tracking/sessions");
        Version(1);
        Policies(EmulatorPermissions.PolicyName(EmulatorPermissions.OpsRead));
        Description(d => d.WithTags("Backoffice"));
    }

    public override Task HandleAsync(StartActivityTrackingSessionRequest req, CancellationToken ct)
    {
        var operatorName = User.Identity?.Name
            ?? User.FindFirstValue("preferred_username")
            ?? "unknown";

        var session = tracker.StartSession(req.SubjectId, req.Reason, operatorName);
        return Send.OkAsync(new StartActivityTrackingSessionResponse { Session = session }, ct);
    }
}
