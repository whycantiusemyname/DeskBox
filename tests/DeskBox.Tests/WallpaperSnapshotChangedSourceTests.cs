using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WallpaperSnapshotChangedSourceTests
{
    [Fact]
    public void Publish_DispatchesSnapshotVersionToSubscribedClient()
    {
        var source = new WallpaperSnapshotChangedSource();
        var dispatched = new List<Action>();
        var received = new List<long>();
        using IDisposable subscription = source.Subscribe(
            action =>
            {
                dispatched.Add(action);
                return true;
            },
            received.Add);

        source.Publish(17);

        Assert.Empty(received);
        Assert.Single(dispatched);
        dispatched[0]();
        Assert.Equal(new long[] { 17 }, received);
    }

    [Fact]
    public void PublishWhileDispatcherIsPending_QueuesOnceAndDeliversLatestVersion()
    {
        var source = new WallpaperSnapshotChangedSource();
        var dispatched = new List<Action>();
        var received = new List<long>();
        using IDisposable subscription = source.Subscribe(
            action =>
            {
                dispatched.Add(action);
                return true;
            },
            received.Add);

        source.Publish(17);
        source.Publish(18);
        source.Publish(19);

        Assert.Single(dispatched);
        dispatched[0]();
        Assert.Equal(new long[] { 19 }, received);
    }

    [Fact]
    public void RejectedDispatch_DoesNotStrandSubscriptionAndRetriesOnNextPublish()
    {
        var source = new WallpaperSnapshotChangedSource();
        var dispatched = new List<Action>();
        var received = new List<long>();
        int dispatchAttempts = 0;
        using IDisposable subscription = source.Subscribe(
            action =>
            {
                if (Interlocked.Increment(ref dispatchAttempts) == 1)
                {
                    return false;
                }

                dispatched.Add(action);
                return true;
            },
            received.Add);

        source.Publish(17);
        source.Publish(18);

        Assert.Equal(2, dispatchAttempts);
        Assert.Single(dispatched);
        dispatched[0]();
        Assert.Equal(new long[] { 18 }, received);
    }

    [Fact]
    public void RejectedDispatchWithConcurrentNewVersion_RetriesTheLatestVersion()
    {
        var source = new WallpaperSnapshotChangedSource();
        var dispatched = new List<Action>();
        var received = new List<long>();
        int dispatchAttempts = 0;
        using IDisposable subscription = source.Subscribe(
            action =>
            {
                if (Interlocked.Increment(ref dispatchAttempts) == 1)
                {
                    source.Publish(18);
                    return false;
                }

                dispatched.Add(action);
                return true;
            },
            received.Add);

        source.Publish(17);

        Assert.Equal(2, dispatchAttempts);
        Assert.Single(dispatched);
        dispatched[0]();
        Assert.Equal(new long[] { 18 }, received);
    }

    [Fact]
    public void ObserverException_DoesNotStrandSubscription()
    {
        var source = new WallpaperSnapshotChangedSource();
        var dispatched = new List<Action>();
        var received = new List<long>();
        using IDisposable subscription = source.Subscribe(
            action =>
            {
                dispatched.Add(action);
                return true;
            },
            version =>
            {
                if (version == 17)
                {
                    throw new InvalidOperationException("expected test failure");
                }

                received.Add(version);
            });

        source.Publish(17);
        dispatched[0]();
        source.Publish(18);

        Assert.Equal(2, dispatched.Count);
        dispatched[1]();
        Assert.Equal(new long[] { 18 }, received);
    }

    [Fact]
    public void DisposingSubscription_SuppressesAlreadyQueuedSnapshotAndFutureSnapshots()
    {
        var source = new WallpaperSnapshotChangedSource();
        var dispatched = new List<Action>();
        var received = new List<long>();
        IDisposable subscription = source.Subscribe(
            action =>
            {
                dispatched.Add(action);
                return true;
            },
            received.Add);

        source.Publish(3);
        subscription.Dispose();
        dispatched[0]();
        source.Publish(4);

        Assert.Empty(received);
        Assert.Single(dispatched);
    }
}
