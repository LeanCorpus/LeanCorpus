using Rowles.LeanCorpus.Store;

namespace Rowles.LeanCorpus.Index.Segment;

/// <summary>
/// Thread-safe bounded LRU whose leases prevent active values from being evicted.
/// </summary>
internal sealed class BoundedLruCache<TKey, TValue> : IDisposable, ILifetimeLeaseOwner
    where TKey : notnull
    where TValue : class, IDisposable
{
    private readonly int _capacity;
    private readonly long _maxRetainedBytes;
    private readonly Func<TValue, SegmentReaderCacheResourceUsage>? _resourceUsageSelector;
    private readonly Dictionary<TKey, Entry> _entries;
    private readonly LinkedList<Entry> _lru = [];
    private readonly Lock _lock = new();
    private readonly Action<AggregateException>? _cleanupFailureReporter;
    private SegmentReaderCacheResourceUsage _retainedResources;
    private long _loadCount;
    private long _evictionCount;
    private bool _disposed;

    internal BoundedLruCache(
        int capacity,
        IEqualityComparer<TKey>? comparer = null,
        Action<AggregateException>? cleanupFailureReporter = null,
        long maxRetainedBytes = long.MaxValue,
        Func<TValue, SegmentReaderCacheResourceUsage>? resourceUsageSelector = null)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Cache capacity must be at least one.");
        if (maxRetainedBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxRetainedBytes), maxRetainedBytes,
                "Cache retained-byte capacity must be at least one.");
        _capacity = capacity;
        _maxRetainedBytes = maxRetainedBytes;
        _resourceUsageSelector = resourceUsageSelector;
        _entries = new Dictionary<TKey, Entry>(capacity, comparer);
        _cleanupFailureReporter = cleanupFailureReporter;
    }

    internal int Count { get { lock (_lock) return _entries.Count; } }

    internal SegmentReaderCacheResourceMetrics Metrics
    {
        get
        {
            lock (_lock)
                return new SegmentReaderCacheResourceMetrics(
                    _entries.Count, _evictionCount, _retainedResources.TotalBytes,
                    _maxRetainedBytes, _retainedResources);
        }
    }

    internal long LoadCount => Volatile.Read(ref _loadCount);

    internal Lease Acquire(TKey key, Func<TValue> valueFactory)
    {
        Entry entry;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(key, out entry!))
            {
                entry.LeaseCount++;
                Touch(entry);
            }
            else
            {
                entry = new Entry(key, valueFactory) { LeaseCount = 1 };
                entry.Node = _lru.AddFirst(entry);
                _entries.Add(key, entry);
            }
        }

        TValue value;
        try
        {
            value = entry.GetValue();
            if (entry.MarkLoaded())
                Interlocked.Increment(ref _loadCount);
            RefreshResourceUsage(entry, value);
        }
        catch
        {
            RetireFailedAcquire(entry);
            throw;
        }

        var lease = new Lease(this, entry, value);
        Trim();
        return lease;
    }

    private void RetireFailedAcquire(Entry entry)
    {
        List<TValue>? toDispose = null;
        lock (_lock)
        {
            if (entry.LeaseCount > 0)
                entry.LeaseCount--;
            RetireEntry(entry);
            AddForDisposal(ref toDispose, TakeRetiredValue(entry));
        }

        ReportDisposalFailure(DisposeValues(toDispose));
    }

    private void Release(Entry entry)
    {
        Exception? accountingFailure = null;
        if (_resourceUsageSelector is not null && IsTracked(entry) && entry.TryGetCreated(out var value))
        {
            try
            {
                RefreshResourceUsage(entry, value);
            }
            catch (Exception exception)
            {
                accountingFailure = exception;
            }
        }

        List<TValue>? toDispose = null;
        lock (_lock)
        {
            if (entry.LeaseCount > 0)
                entry.LeaseCount--;
            if (accountingFailure is not null)
                RetireEntry(entry);

            AddForDisposal(ref toDispose, TakeRetiredValue(entry));
            List<TValue>? evicted = CollectEvictions();
            if (evicted is not null)
                (toDispose ??= []).AddRange(evicted);
        }

        AggregateException? disposalFailure = DisposeValues(toDispose);
        ReportDisposalFailure(disposalFailure);
        if (accountingFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(accountingFailure).Throw();
    }

    void ILifetimeLeaseOwner.ReleaseLease(object token) => Release((Entry)token);

    private bool IsTracked(Entry entry)
    {
        lock (_lock)
            return _entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry);
    }

    private void RefreshResourceUsage(Entry entry, TValue value)
    {
        if (_resourceUsageSelector is null)
            return;

        if (!IsTracked(entry))
            return;

        SegmentReaderCacheResourceUsage resources = _resourceUsageSelector(value);
        lock (_lock)
        {
            if (!_entries.TryGetValue(entry.Key, out var current) || !ReferenceEquals(current, entry))
                return;

            _retainedResources -= entry.Resources;
            entry.Resources = resources;
            _retainedResources += resources;
        }
    }

    private void RetireEntry(Entry entry)
    {
        if (entry.Retired)
            return;

        entry.Retired = true;
        if (_entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
        {
            _entries.Remove(entry.Key);
            _retainedResources -= entry.Resources;
            entry.Resources = default;
        }

        if (entry.Node is not null)
        {
            _lru.Remove(entry.Node);
            entry.Node = null;
        }
    }

    private TValue? TakeRetiredValue(Entry entry)
    {
        if (!entry.Retired || entry.LeaseCount != 0 || entry.DisposalStarted)
            return null;

        entry.DisposalStarted = true;
        return entry.TryGetCreated(out var value) ? value : null;
    }

    private static void AddForDisposal(ref List<TValue>? values, TValue? value)
    {
        if (value is not null)
            (values ??= []).Add(value);
    }

    private void Trim()
    {
        List<TValue>? toDispose;
        lock (_lock)
            toDispose = CollectEvictions();
        ReportDisposalFailure(DisposeValues(toDispose));
    }

    private List<TValue>? CollectEvictions()
    {
        List<TValue>? values = null;
        while (_entries.Count > (_disposed ? 0 : _capacity)
            || (!_disposed && _retainedResources.TotalBytes > _maxRetainedBytes))
        {
            var node = _lru.Last;
            while (node is not null && node.Value.LeaseCount != 0)
                node = node.Previous;
            if (node is null)
                break;

            var entry = node.Value;
            RetireEntry(entry);
            _evictionCount++;
            AddForDisposal(ref values, TakeRetiredValue(entry));
        }
        return values;
    }

    private void Touch(Entry entry)
    {
        if (entry.Node is null || ReferenceEquals(entry.Node, _lru.First))
            return;
        _lru.Remove(entry.Node);
        _lru.AddFirst(entry.Node);
    }

    private AggregateException? DisposeValues(List<TValue>? values)
    {
        if (values is null)
            return null;

        List<Exception>? failures = null;
        foreach (var value in values)
        {
            try
            {
                value.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        return failures is null
            ? null
            : new AggregateException("One or more cached values failed to dispose.", failures);
    }

    private void ReportDisposalFailure(AggregateException? failure)
    {
        if (failure is null)
            return;

        if (_cleanupFailureReporter is null)
        {
            TraceCleanupFailure("Bounded LRU cache cleanup failed: {0}", failure);
            return;
        }

        try
        {
            _cleanupFailureReporter(failure);
        }
        catch (Exception reporterFailure)
        {
            var combined = new AggregateException(
                "The cache cleanup failure reporter also failed.", failure, reporterFailure);
            TraceCleanupFailure("Bounded LRU cache cleanup reporting failed: {0}", combined);
        }
    }

    private static void TraceCleanupFailure(string message, Exception failure)
    {
        try
        {
            System.Diagnostics.Trace.TraceError(message, failure);
        }
        catch
        {
            // Diagnostic listeners must not replace the cache operation's failure.
        }
    }

    public void Dispose()
    {
        List<TValue>? toDispose;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            toDispose = CollectEvictions();
        }
        var disposalFailure = DisposeValues(toDispose);
        if (disposalFailure is not null)
            throw disposalFailure;
    }

    internal readonly struct Lease : IDisposable
    {
        private readonly LeaseToken? _token;

        internal Lease(BoundedLruCache<TKey, TValue> owner, Entry entry, TValue value)
        {
            _token = new LeaseToken(owner, entry);
            Value = value;
        }

        internal TValue Value { get; }

        public readonly void Dispose() => _token?.Dispose();

        internal readonly LifetimeLease Detach() => _token?.Detach() ?? default;

        private sealed class LeaseToken
        {
            private const int Active = 0;
            private const int Detached = 1;
            private const int Released = 2;

            private readonly BoundedLruCache<TKey, TValue> _owner;
            private readonly Entry _entry;
            private int _state;

            internal LeaseToken(BoundedLruCache<TKey, TValue> owner, Entry entry)
            {
                _owner = owner;
                _entry = entry;
            }

            internal void Dispose()
            {
                if (Interlocked.CompareExchange(ref _state, Released, Active) == Active)
                    _owner.Release(_entry);
            }

            internal LifetimeLease Detach()
            {
                return Interlocked.CompareExchange(ref _state, Detached, Active) == Active
                    ? new LifetimeLease(_owner, _entry)
                    : default;
            }
        }
    }

    internal sealed class Entry
    {
        private int _loaded;
        private readonly Func<TValue> _valueFactory;
        private TValue? _value;
        private Exception? _loadException;
        private bool _loading;

        internal Entry(TKey key, Func<TValue> valueFactory)
        {
            Key = key;
            _valueFactory = valueFactory;
        }

        internal TKey Key { get; }
        internal int LeaseCount { get; set; }
        internal LinkedListNode<Entry>? Node { get; set; }
        internal SegmentReaderCacheResourceUsage Resources { get; set; }
        internal bool Retired { get; set; }
        internal bool DisposalStarted { get; set; }

        internal bool MarkLoaded() => Interlocked.Exchange(ref _loaded, 1) == 0;

        internal TValue GetValue()
        {
            lock (this)
            {
                while (_loading)
                    Monitor.Wait(this);
                if (_value is not null)
                    return _value;
                if (_loadException is not null)
                    throw new InvalidOperationException("The cached value failed to load.", _loadException);
                _loading = true;
            }

            try
            {
                var value = _valueFactory();
                lock (this)
                {
                    _value = value;
                    _loading = false;
                    Monitor.PulseAll(this);
                    return value;
                }
            }
            catch (Exception ex)
            {
                lock (this)
                {
                    _loadException = ex;
                    _loading = false;
                    Monitor.PulseAll(this);
                }
                throw;
            }
        }

        internal bool TryGetCreated([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TValue? value)
        {
            lock (this)
            {
                value = _value;
                return value is not null;
            }
        }
    }
}
