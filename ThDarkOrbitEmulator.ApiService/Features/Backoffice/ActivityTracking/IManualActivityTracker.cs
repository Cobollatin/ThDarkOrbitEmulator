using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace ThDarkOrbitEmulator.ApiService.Features.Backoffice.ActivityTracking;

public interface IManualActivityTracker
{
    ActivityTrackingSession StartSession(string subjectId, string reason, string operatorName);
    bool TryGetSession(string sessionId, out ActivityTrackingSession session);
    IReadOnlyList<ActivityTrackingEvent> GetSnapshot(string sessionId);
    ActivityTrackingEvent AppendEvent(string sessionId, string type, string source, string details, DateTimeOffset occurredAtUtc);
    IAsyncEnumerable<ActivityTrackingEvent> SubscribeAsync(string sessionId, CancellationToken cancellationToken);
}

public sealed class ManualActivityTracker : IManualActivityTracker
{
    private const int MaxEventsPerSession = 250;
    private readonly TimeProvider _clock;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TrackedSession> _sessions = new(StringComparer.Ordinal);

    public ManualActivityTracker(TimeProvider clock)
    {
        _clock = clock;
    }

    public ActivityTrackingSession StartSession(string subjectId, string reason, string operatorName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorName);

        var session = new ActivityTrackingSession(
            Id: Guid.NewGuid().ToString("N"),
            SubjectId: subjectId.Trim(),
            OperatorName: operatorName.Trim(),
            Reason: reason.Trim(),
            StartedAtUtc: _clock.GetUtcNow());

        _sessions[session.Id] = new TrackedSession(session);
        return session;
    }

    public bool TryGetSession(string sessionId, out ActivityTrackingSession session)
    {
        if (_sessions.TryGetValue(sessionId, out var trackedSession))
        {
            session = trackedSession.Session;
            return true;
        }

        session = null!;
        return false;
    }

    public IReadOnlyList<ActivityTrackingEvent> GetSnapshot(string sessionId)
    {
        return GetTrackedSession(sessionId).Snapshot();
    }

    public ActivityTrackingEvent AppendEvent(
        string sessionId,
        string type,
        string source,
        string details,
        DateTimeOffset occurredAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        return GetTrackedSession(sessionId).Append(type.Trim(), source.Trim(), details.Trim(), occurredAtUtc);
    }

    public async IAsyncEnumerable<ActivityTrackingEvent> SubscribeAsync(
        string sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var trackedSession = GetTrackedSession(sessionId);
        var channel = Channel.CreateUnbounded<ActivityTrackingEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var replay = trackedSession.AddSubscriber(channel.Writer);

        try
        {
            foreach (var activityEvent in replay)
            {
                yield return activityEvent;
            }

            while (await channel.Reader.WaitToReadAsync(cancellationToken))
            {
                while (channel.Reader.TryRead(out var activityEvent))
                {
                    yield return activityEvent;
                }
            }
        }
        finally
        {
            trackedSession.RemoveSubscriber(channel.Writer);
        }
    }

    private TrackedSession GetTrackedSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return _sessions.TryGetValue(sessionId, out var session)
            ? session
            : throw new KeyNotFoundException($"Activity tracking session '{sessionId}' was not found.");
    }

    private sealed class TrackedSession
    {
        private readonly object _gate = new();
        private readonly Queue<ActivityTrackingEvent> _events = new();
        private readonly HashSet<ChannelWriter<ActivityTrackingEvent>> _subscribers = [];
        private long _sequence;

        public TrackedSession(ActivityTrackingSession session)
        {
            Session = session;
        }

        public ActivityTrackingSession Session { get; }

        public ActivityTrackingEvent Append(string type, string source, string details, DateTimeOffset occurredAtUtc)
        {
            lock (_gate)
            {
                var activityEvent = new ActivityTrackingEvent(
                    Sequence: ++_sequence,
                    SessionId: Session.Id,
                    Type: type,
                    Source: source,
                    Details: details,
                    OccurredAtUtc: occurredAtUtc);

                _events.Enqueue(activityEvent);
                while (_events.Count > MaxEventsPerSession)
                {
                    _events.Dequeue();
                }

                foreach (var subscriber in _subscribers)
                {
                    subscriber.TryWrite(activityEvent);
                }

                return activityEvent;
            }
        }

        public IReadOnlyList<ActivityTrackingEvent> Snapshot()
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }

        public IReadOnlyList<ActivityTrackingEvent> AddSubscriber(ChannelWriter<ActivityTrackingEvent> subscriber)
        {
            lock (_gate)
            {
                _subscribers.Add(subscriber);
                return _events.ToArray();
            }
        }

        public void RemoveSubscriber(ChannelWriter<ActivityTrackingEvent> subscriber)
        {
            lock (_gate)
            {
                _subscribers.Remove(subscriber);
            }

            subscriber.TryComplete();
        }
    }
}
