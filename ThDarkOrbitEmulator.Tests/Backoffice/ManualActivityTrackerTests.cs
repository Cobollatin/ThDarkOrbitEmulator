using ThDarkOrbitEmulator.ApiService.Features.Backoffice.ActivityTracking;

namespace ThDarkOrbitEmulator.Tests.Backoffice;

public class ManualActivityTrackerTests
{
    [Test]
    public void StartSession_DoesNotRequireIdentityDomainUser()
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);

        var session = tracker.StartSession("connection:alpha", "manual support trace", "operator");

        Assert.Multiple(() =>
        {
            Assert.That(session.Id, Is.Not.Empty);
            Assert.That(session.SubjectId, Is.EqualTo("connection:alpha"));
            Assert.That(session.OperatorName, Is.EqualTo("operator"));
            Assert.That(session.Reason, Is.EqualTo("manual support trace"));
        });
    }

    [Test]
    public void AppendEvent_AddsOrderedBoundedSnapshot()
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        var session = tracker.StartSession("socket-42", "investigate reconnect loop", "operator");

        var first = tracker.AppendEvent(session.Id, "connected", "gateway", "handshake accepted", DateTimeOffset.UtcNow);
        var second = tracker.AppendEvent(session.Id, "map.entered", "game", "map 1-1", DateTimeOffset.UtcNow);

        var snapshot = tracker.GetSnapshot(session.Id);

        Assert.Multiple(() =>
        {
            Assert.That(first.Sequence, Is.EqualTo(1));
            Assert.That(second.Sequence, Is.EqualTo(2));
            Assert.That(snapshot.Select(activityEvent => activityEvent.Type), Is.EqualTo(new[] { "connected", "map.entered" }));
        });
    }

    [Test]
    public async Task SubscribeAsync_ReplaysSnapshotAndStreamsFutureEvents()
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        var session = tracker.StartSession("subject-1", "watch live behavior", "operator");
        var existing = tracker.AppendEvent(session.Id, "existing", "api", "already captured", DateTimeOffset.UtcNow);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = tracker.SubscribeAsync(session.Id, cts.Token).GetAsyncEnumerator(cts.Token);

        Assert.That(await ReadNextAsync(enumerator), Is.EqualTo(existing));

        var live = tracker.AppendEvent(session.Id, "live", "gateway", "streamed while subscribed", DateTimeOffset.UtcNow);

        Assert.That(await ReadNextAsync(enumerator), Is.EqualTo(live));
    }

    [TestCase("", "reason", "operator")]
    [TestCase("subject", "  ", "operator")]
    [TestCase("subject", "reason", "\t")]
    public void StartSession_RejectsBlankRequiredFields(string subject, string reason, string operatorName)
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        Assert.Throws<ArgumentException>(() => tracker.StartSession(subject, reason, operatorName));
    }

    [Test]
    public void StartSession_TrimsFieldsAndKeepsSessionsIndependent()
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        var first = tracker.StartSession(" subject ", " reason ", " operator ");
        var second = tracker.StartSession("subject", "reason", "operator");
        var activityEvent = tracker.AppendEvent(first.Id, " type ", " source ", " details ", DateTimeOffset.UnixEpoch);

        Assert.Multiple(() =>
        {
            Assert.That(first.Id, Is.Not.EqualTo(second.Id));
            Assert.That(first.SubjectId, Is.EqualTo("subject"));
            Assert.That(first.Reason, Is.EqualTo("reason"));
            Assert.That(first.OperatorName, Is.EqualTo("operator"));
            Assert.That(tracker.TryGetSession(first.Id, out var found), Is.True);
            Assert.That(found, Is.EqualTo(first));
            Assert.That(activityEvent.Type, Is.EqualTo("type"));
            Assert.That(activityEvent.Source, Is.EqualTo("source"));
            Assert.That(activityEvent.Details, Is.EqualTo("details"));
            Assert.That(activityEvent.OccurredAtUtc, Is.EqualTo(DateTimeOffset.UnixEpoch));
            Assert.That(tracker.GetSnapshot(second.Id), Is.Empty);
            Assert.That(tracker.TryGetSession("missing", out _), Is.False);
        });
    }

    [TestCase("", "source")]
    [TestCase("type", " ")]
    public void AppendEvent_RejectsBlankTypeOrSource(string type, string source)
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        var session = tracker.StartSession("subject", "reason", "operator");
        Assert.Throws<ArgumentException>(() => tracker.AppendEvent(session.Id, type, source, "details", DateTimeOffset.UnixEpoch));
        Assert.That(tracker.GetSnapshot(session.Id), Is.Empty);
    }

    [Test]
    public void Snapshot_RetainsLast250EventsWithMonotonicSequence()
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        var session = tracker.StartSession("subject", "reason", "operator");
        for (var number = 1; number <= 252; number++)
        {
            tracker.AppendEvent(session.Id, "event", "test", number.ToString(), DateTimeOffset.UnixEpoch);
        }

        var snapshot = tracker.GetSnapshot(session.Id);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot, Has.Count.EqualTo(250));
            Assert.That(snapshot.Select(item => item.Sequence), Is.EqualTo(Enumerable.Range(3, 250).Select(i => (long)i)));
            Assert.That(snapshot.All(item => item.SessionId == session.Id), Is.True);
        });
    }

    [Test]
    public void UnknownSession_RejectsSnapshotAndAppend()
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        Assert.Throws<KeyNotFoundException>(() => tracker.GetSnapshot("missing"));
        Assert.Throws<KeyNotFoundException>(() => tracker.AppendEvent("missing", "event", "test", "", DateTimeOffset.UnixEpoch));
    }

    [Test]
    public void StartSession_UsesInjectedClockAndCreatesGuidWithoutDashes()
    {
        var instant = DateTimeOffset.Parse("2026-10-25T03:04:05+00:00");
        var tracker = new ManualActivityTracker(new FixedTimeProvider(instant));

        var session = tracker.StartSession("pilot", "trace", "operator");

        Assert.Multiple(() =>
        {
            Assert.That(session.StartedAtUtc, Is.EqualTo(instant));
            Assert.That(session.Id, Has.Length.EqualTo(32));
            Assert.That(Guid.TryParseExact(session.Id, "N", out _), Is.True);
        });
    }

    [Test]
    public void Snapshot_IsAnIndependentPointInTimeCopy()
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        var session = tracker.StartSession("pilot", "trace", "operator");
        var first = tracker.AppendEvent(session.Id, "first", "test", "", DateTimeOffset.UnixEpoch);
        var snapshot = tracker.GetSnapshot(session.Id);

        tracker.AppendEvent(session.Id, "second", "test", "", DateTimeOffset.UnixEpoch);

        Assert.That(snapshot, Is.EqualTo(new[] { first }));
        Assert.That(tracker.GetSnapshot(session.Id), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task SubscribeAsync_ReplaysOnlyRetainedEventsThenStreamsNextSequence()
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        var session = tracker.StartSession("pilot", "trace", "operator");
        for (var i = 0; i < 251; i++)
        {
            tracker.AppendEvent(session.Id, "event", "test", "", DateTimeOffset.UnixEpoch);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = tracker.SubscribeAsync(session.Id, cts.Token).GetAsyncEnumerator(cts.Token);
        var replayed = new List<long>();
        for (var i = 0; i < 250; i++)
        {
            replayed.Add((await ReadNextAsync(enumerator)).Sequence);
        }

        var live = tracker.AppendEvent(session.Id, "live", "test", "", DateTimeOffset.UnixEpoch);
        Assert.Multiple(() =>
        {
            Assert.That(replayed, Is.EqualTo(Enumerable.Range(2, 250).Select(i => (long)i)));
            Assert.That(live.Sequence, Is.EqualTo(252));
        });
        Assert.That(await ReadNextAsync(enumerator), Is.EqualTo(live));
    }

    [Test]
    public async Task ConcurrentAppends_AssignUniqueContiguousPerSessionSequences()
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        var first = tracker.StartSession("first", "trace", "operator");
        var second = tracker.StartSession("second", "trace", "operator");

        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() =>
        {
            tracker.AppendEvent(first.Id, "event", "test", i.ToString(), DateTimeOffset.UnixEpoch);
            tracker.AppendEvent(second.Id, "event", "test", i.ToString(), DateTimeOffset.UnixEpoch);
        })));

        foreach (var session in new[] { first, second })
        {
            Assert.That(tracker.GetSnapshot(session.Id).Select(item => item.Sequence),
                Is.EqualTo(Enumerable.Range(1, 100).Select(i => (long)i)));
        }
    }

    [Test]
    public async Task SubscribeAsync_StreamsConcurrentAppendsOnceInSequenceOrder()
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        var session = tracker.StartSession("pilot", "trace", "operator");
        tracker.AppendEvent(session.Id, "initial", "test", "", DateTimeOffset.UnixEpoch);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = tracker.SubscribeAsync(session.Id, cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.That((await ReadNextAsync(enumerator)).Sequence, Is.EqualTo(1));

        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() =>
            tracker.AppendEvent(session.Id, "event", "test", i.ToString(), DateTimeOffset.UnixEpoch))));

        var sequences = new List<long>();
        for (var i = 0; i < 100; i++)
        {
            sequences.Add((await ReadNextAsync(enumerator)).Sequence);
        }

        Assert.That(sequences, Is.EqualTo(Enumerable.Range(2, 100).Select(i => (long)i)));
    }

    [Test]
    public async Task SubscribeAsync_FansOutToEachSubscriberWithoutCrossingSessionBoundary()
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        var firstSession = tracker.StartSession("first", "trace", "operator");
        var secondSession = tracker.StartSession("second", "trace", "operator");
        var firstSeed = tracker.AppendEvent(firstSession.Id, "seed", "test", "", DateTimeOffset.UnixEpoch);
        var secondSeed = tracker.AppendEvent(secondSession.Id, "seed", "test", "", DateTimeOffset.UnixEpoch);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var subscriberA = tracker.SubscribeAsync(firstSession.Id, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        await using var subscriberB = tracker.SubscribeAsync(firstSession.Id, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        await using var subscriberOther = tracker.SubscribeAsync(secondSession.Id, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.That(await ReadNextAsync(subscriberA), Is.EqualTo(firstSeed));
        Assert.That(await ReadNextAsync(subscriberB), Is.EqualTo(firstSeed));
        Assert.That(await ReadNextAsync(subscriberOther), Is.EqualTo(secondSeed));

        var firstLive = tracker.AppendEvent(firstSession.Id, "live", "test", "first only", DateTimeOffset.UnixEpoch);
        var secondLive = tracker.AppendEvent(secondSession.Id, "live", "test", "second only", DateTimeOffset.UnixEpoch);

        Assert.Multiple(() =>
        {
            Assert.That(firstLive.Sequence, Is.EqualTo(2));
            Assert.That(secondLive.Sequence, Is.EqualTo(2));
        });
        Assert.That(await ReadNextAsync(subscriberA), Is.EqualTo(firstLive));
        Assert.That(await ReadNextAsync(subscriberB), Is.EqualTo(firstLive));
        Assert.That(await ReadNextAsync(subscriberOther), Is.EqualTo(secondLive));
    }

    [Test]
    public async Task SubscribeAsync_CancellationStopsWaitingAndFutureSubscribersCanReplay()
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        var session = tracker.StartSession("pilot", "trace", "operator");
        var seed = tracker.AppendEvent(session.Id, "seed", "test", "", DateTimeOffset.UnixEpoch);
        using var cancellation = new CancellationTokenSource();
        await using (var subscriber = tracker.SubscribeAsync(session.Id, cancellation.Token).GetAsyncEnumerator(cancellation.Token))
        {
            Assert.That(await ReadNextAsync(subscriber), Is.EqualTo(seed));
            cancellation.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await subscriber.MoveNextAsync());
        }

        var afterDisconnect = tracker.AppendEvent(session.Id, "later", "test", "", DateTimeOffset.UnixEpoch);
        using var nextCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var nextSubscriber = tracker.SubscribeAsync(session.Id, nextCancellation.Token).GetAsyncEnumerator(nextCancellation.Token);
        Assert.That(await ReadNextAsync(nextSubscriber), Is.EqualTo(seed));
        Assert.That(await ReadNextAsync(nextSubscriber), Is.EqualTo(afterDisconnect));
    }

    [Test]
    public async Task SubscribeAsync_ActiveSubscriberReceivesEventsPrunedFromLateReplay()
    {
        var tracker = new ManualActivityTracker(TimeProvider.System);
        var session = tracker.StartSession("pilot", "trace", "operator");
        var seed = tracker.AppendEvent(session.Id, "seed", "test", "", DateTimeOffset.UnixEpoch);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var active = tracker.SubscribeAsync(session.Id, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.That(await ReadNextAsync(active), Is.EqualTo(seed));

        for (var i = 2; i <= 260; i++)
        {
            tracker.AppendEvent(session.Id, "event", "test", i.ToString(), DateTimeOffset.UnixEpoch);
        }

        var snapshot = tracker.GetSnapshot(session.Id);
        Assert.That(snapshot.Select(item => item.Sequence),
            Is.EqualTo(Enumerable.Range(11, 250).Select(i => (long)i)));

        var activeSequences = new List<long>();
        for (var i = 2; i <= 260; i++)
        {
            activeSequences.Add((await ReadNextAsync(active)).Sequence);
        }

        Assert.That(activeSequences, Is.EqualTo(Enumerable.Range(2, 259).Select(i => (long)i)));

        await using var late = tracker.SubscribeAsync(session.Id, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var lateSequences = new List<long>();
        for (var i = 0; i < 250; i++)
        {
            lateSequences.Add((await ReadNextAsync(late)).Sequence);
        }

        Assert.That(lateSequences, Is.EqualTo(Enumerable.Range(11, 250).Select(i => (long)i)));
    }

    private sealed class FixedTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    private static async Task<ActivityTrackingEvent> ReadNextAsync(IAsyncEnumerator<ActivityTrackingEvent> enumerator)
    {
        var moved = await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(moved, Is.True);
        return enumerator.Current;
    }
}
