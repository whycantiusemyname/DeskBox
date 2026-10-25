namespace DeskBox.Services;

/// <summary>Dispatches immutable wallpaper snapshot versions to active UI clients.</summary>
internal sealed class WallpaperSnapshotChangedSource
{
    private readonly object _gate = new();
    private readonly HashSet<Subscription> _subscriptions = [];

    public IDisposable Subscribe(Func<Action, bool> dispatch, Action<long> callback)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(callback);

        var subscription = new Subscription(this, dispatch, callback);
        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }

        return subscription;
    }

    public void Publish(long version)
    {
        Subscription[] subscribers;
        lock (_gate)
        {
            subscribers = _subscriptions.ToArray();
        }

        foreach (Subscription subscription in subscribers)
        {
            subscription.Publish(version);
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
        }
    }

    private sealed class Subscription(
        WallpaperSnapshotChangedSource owner,
        Func<Action, bool> dispatch,
        Action<long> callback) : IDisposable
    {
        private readonly object _gate = new();
        private int _disposed;
        private long _latestVersion;
        private long _dispatchAttempt;
        private bool _hasObservedVersion;
        private bool _hasLatestVersion;
        private bool _pending;

        public void Publish(long version)
        {
            bool shouldDispatch = false;
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                if (!_hasObservedVersion || version >= _latestVersion)
                {
                    _latestVersion = version;
                    _hasObservedVersion = true;
                    _hasLatestVersion = true;
                }

                if (_hasLatestVersion && !_pending)
                {
                    _pending = true;
                    shouldDispatch = true;
                }
            }

            if (shouldDispatch)
            {
                EnqueueLatest();
            }
        }

        private void EnqueueLatest()
        {
            long attempt;
            long attemptedVersion;
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    _pending = false;
                    _hasLatestVersion = false;
                    return;
                }

                if (!_pending || !_hasLatestVersion)
                {
                    return;
                }

                attempt = ++_dispatchAttempt;
                attemptedVersion = _latestVersion;
            }

            bool accepted;
            try
            {
                accepted = dispatch(DrainLatest);
            }
            catch
            {
                accepted = false;
            }

            if (!accepted)
            {
                OnDispatchRejected(attempt, attemptedVersion);
            }
        }

        private void OnDispatchRejected(long attempt, long attemptedVersion)
        {
            bool retryLatest = false;
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0 ||
                    attempt != _dispatchAttempt ||
                    !_pending)
                {
                    return;
                }

                if (_hasLatestVersion && _latestVersion != attemptedVersion)
                {
                    // A newer version raced with this rejected enqueue. Retry
                    // that value once; no UI work was accepted or accumulated.
                    retryLatest = true;
                }
                else
                {
                    // Keep the latest value so a later Publish can retry, but
                    // clear pending so this subscription cannot get stuck.
                    _pending = false;
                }
            }

            if (retryLatest)
            {
                EnqueueLatest();
            }
        }

        private void DrainLatest()
        {
            bool enqueueLatest = false;
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    _pending = false;
                    _hasLatestVersion = false;
                    return;
                }

                if (!_hasLatestVersion)
                {
                    _pending = false;
                    return;
                }

                long version = _latestVersion;
                _hasLatestVersion = false;
                try
                {
                    // Run under the subscription gate so Dispose either
                    // prevents this callback or waits for it to finish.
                    callback(version);
                }
                catch
                {
                    // A client callback must not strand the pending state or
                    // fail the wallpaper capture worker.
                }

                if (Volatile.Read(ref _disposed) != 0)
                {
                    _pending = false;
                    _hasLatestVersion = false;
                }
                else if (_hasLatestVersion)
                {
                    enqueueLatest = true;
                }
                else
                {
                    _pending = false;
                }
            }

            if (enqueueLatest)
            {
                EnqueueLatest();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (_gate)
            {
                _pending = false;
                _hasLatestVersion = false;
            }

            owner.Remove(this);
        }
    }
}
