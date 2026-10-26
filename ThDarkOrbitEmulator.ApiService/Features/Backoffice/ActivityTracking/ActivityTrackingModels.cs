namespace ThDarkOrbitEmulator.ApiService.Features.Backoffice.ActivityTracking;

public sealed record ActivityTrackingSession(
    string Id,
    string SubjectId,
    string OperatorName,
    string Reason,
    DateTimeOffset StartedAtUtc);

public sealed record ActivityTrackingEvent(
    long Sequence,
    string SessionId,
    string Type,
    string Source,
    string Details,
    DateTimeOffset OccurredAtUtc);

public sealed class StartActivityTrackingSessionRequest
{
    public string SubjectId { get; init; } = "";
    public string Reason { get; init; } = "";
}

public sealed class StartActivityTrackingSessionResponse
{
    public ActivityTrackingSession Session { get; init; } = null!;
}

public sealed class AppendActivityTrackingEventRequest
{
    public string Type { get; init; } = "";
    public string Source { get; init; } = "";
    public string Details { get; init; } = "";
    public DateTimeOffset? OccurredAtUtc { get; init; }
}

public sealed class AppendActivityTrackingEventResponse
{
    public ActivityTrackingEvent Event { get; init; } = null!;
}

public sealed class ActivityTrackingSnapshotResponse
{
    public ActivityTrackingSession Session { get; init; } = null!;
    public IReadOnlyList<ActivityTrackingEvent> Events { get; init; } = [];
}
