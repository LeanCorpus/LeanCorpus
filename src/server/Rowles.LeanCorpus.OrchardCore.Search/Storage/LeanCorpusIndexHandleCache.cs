using System.Collections.Concurrent;
using Rowles.LeanCorpus.Document;
using Rowles.LeanCorpus.Index.Indexer;
using Rowles.LeanCorpus.Search.Searcher;
using Rowles.LeanCorpus.Store;
using Rowles.LeanCorpus.Analysis.Analysers;
using Rowles.LeanCorpus.Analysis;
using Rowles.LeanCorpus.OrchardCore.Search.Indexing;

namespace Rowles.LeanCorpus.OrchardCore.Search.Storage;

/// <summary>Owns one writer and a refreshed search snapshot for each tenant-local index.</summary>
internal sealed class LeanCorpusIndexHandleCache : IDisposable, IAsyncDisposable
{
    private static readonly object SharedStateLock = new();
    private static SharedState? _sharedState;
    private readonly SharedState _state;
    private int _disposed;

    public LeanCorpusIndexHandleCache()
    {
        while (true)
        {
            Task? closing = null;
            lock (SharedStateLock)
            {
                _state = _sharedState ??= new SharedState();
                if (!_state.IsClosing)
                {
                    _state.ReferenceCount++;
                    return;
                }

                closing = _state.Closed.Task;
            }

            closing.GetAwaiter().GetResult();
        }
    }

    public async Task<T> WithExclusiveAsync<T>(LeanCorpusIndexPaths paths, Func<Entry, T> operation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var entry = _state.Entries.GetOrAdd(paths.IndexDirectory, _ => new Entry(paths));
        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return operation(entry);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async Task<T> WithExclusiveAsync<T>(LeanCorpusIndexPaths paths, Func<Entry, Task<T>> operation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var entry = _state.Entries.GetOrAdd(paths.IndexDirectory, _ => new Entry(paths));
        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await operation(entry).ConfigureAwait(false);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async Task<T> WithSearcherAsync<T>(
        LeanCorpusIndexPaths paths,
        Func<IndexSearcher, T> operation,
        LeanCorpusSchemaManifest? manifest = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var entry = _state.Entries.GetOrAdd(paths.IndexDirectory, _ => new Entry(paths));
        await entry.Gate.WaitAsync().ConfigureAwait(false);
        SearcherLease lease;
        try
        {
            lease = entry.Open(createIfMissing: false, manifest).AcquireSearcher();
        }
        finally
        {
            entry.Gate.Release();
        }

        using (lease)
            return operation(lease.Searcher);
    }

    public async Task<T> WithSearcherAsync<T>(
        LeanCorpusIndexPaths paths,
        Func<IndexSearcher, LeanCorpusSchemaManifest, T> operation,
        Func<Entry, Task<LeanCorpusSchemaManifest>> manifestFactory)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var entry = _state.Entries.GetOrAdd(paths.IndexDirectory, _ => new Entry(paths));
        await entry.Gate.WaitAsync().ConfigureAwait(false);
        SearcherLease lease;
        LeanCorpusSchemaManifest manifest;
        try
        {
            manifest = await manifestFactory(entry).ConfigureAwait(false);
            lease = entry.Open(createIfMissing: false, manifest).AcquireSearcher();
        }
        finally
        {
            entry.Gate.Release();
        }

        using (lease)
            return operation(lease.Searcher, manifest);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        bool closeState = false;
        lock (SharedStateLock)
        {
            _state.ReferenceCount--;
            if (_state.ReferenceCount == 0 && !_state.IsClosing)
            {
                _state.IsClosing = true;
                closeState = true;
            }
        }

        if (!closeState)
            return;

        try
        {
            foreach (var entry in _state.Entries.Values)
            {
                await entry.Gate.WaitAsync().ConfigureAwait(false);
                try { entry.Close(); }
                finally { entry.Gate.Release(); }
            }

            _state.Entries.Clear();
        }
        finally
        {
            lock (SharedStateLock)
            {
                if (ReferenceEquals(_sharedState, _state))
                    _sharedState = null;
                _state.Closed.TrySetResult();
            }
        }
    }

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class SharedState
    {
        internal ConcurrentDictionary<string, Entry> Entries { get; } = new(StringComparer.Ordinal);
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ReferenceCount;
        internal bool IsClosing;
    }

    internal sealed class Entry(LeanCorpusIndexPaths paths)
    {
        private LeanCorpusIndexHandle? _handle;

        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal string IndexDirectory => paths.IndexDirectory;
        internal LeanCorpusIndexHandle? Handle => _handle;

        internal LeanCorpusIndexHandle Open(bool createIfMissing, LeanCorpusSchemaManifest? manifest = null)
        {
            if (_handle is not null)
            {
                if (!Directory.Exists(paths.IndexDirectory))
                {
                    Close();
                    if (!createIfMissing)
                        throw new DirectoryNotFoundException($"LeanCorpus index '{paths.IndexFullName}' does not exist.");
                }
                else if (!HasCommit(paths.IndexDirectory))
                {
                    Close();
                    if (!createIfMissing)
                        throw new InvalidDataException($"LeanCorpus index '{paths.IndexFullName}' has no committed index state.");
                }
                else if (manifest is null || _handle.DefaultAnalyserName == manifest.Metadata.DefaultAnalyser)
                {
                    return _handle;
                }
                else
                {
                    Close();
                }
            }

            bool directoryExisted = Directory.Exists(paths.IndexDirectory);
            if (!directoryExisted && !createIfMissing)
                throw new DirectoryNotFoundException($"LeanCorpus index '{paths.IndexFullName}' does not exist.");
            if (directoryExisted && !createIfMissing && !HasCommit(paths.IndexDirectory))
                throw new InvalidDataException($"LeanCorpus index '{paths.IndexFullName}' has no committed index state.");

            var directory = new MMapDirectory(paths.IndexDirectory);
            IndexWriter? writer = null;
            IndexSearcher? searcher = null;
            try
            {
                string analyserName = manifest?.Metadata.DefaultAnalyser ?? "standard";
                IAnalyser defaultAnalyser = analyserName switch
                {
                    "keyword" => new KeywordAnalyser(),
                    "standard" => new StandardAnalyser(),
                    _ => throw new InvalidDataException("The LeanCorpus index metadata names an unsupported analyser."),
                };
                var configuration = new IndexWriterConfig
                {
                    DurableCommits = true,
                    DefaultAnalyser = defaultAnalyser,
                };
                writer = new IndexWriter(directory, configuration);
                if (!HasCommit(paths.IndexDirectory))
                {
                    if (!createIfMissing)
                        throw new InvalidDataException($"LeanCorpus index '{paths.IndexFullName}' has no committed index state.");
                    writer.Commit();
                }

                searcher = new IndexSearcher(directory);
                _handle = new LeanCorpusIndexHandle(directory, writer, searcher, analyserName);
                return _handle;
            }
            catch
            {
                searcher?.Dispose();
                writer?.Dispose();
                directory.Dispose();
                throw;
            }
        }

        internal void Close()
        {
            _handle?.Dispose();
            _handle = null;
        }

        private static bool HasCommit(string path)
            => Directory.Exists(path) && Directory.EnumerateFiles(path, "segments_*").Any();
    }
}

internal sealed class LeanCorpusIndexHandle : IDisposable
{
    private readonly ReaderWriterLockSlim _searcherLock = new(LockRecursionPolicy.NoRecursion);
    private IndexSearcher _searcher;
    private int _disposed;

    public MMapDirectory Directory { get; }
    public IndexWriter Writer { get; }
    public string DefaultAnalyserName { get; }

    public LeanCorpusIndexHandle(MMapDirectory directory, IndexWriter writer, IndexSearcher searcher, string defaultAnalyserName)
    {
        Directory = directory;
        Writer = writer;
        _searcher = searcher;
        DefaultAnalyserName = defaultAnalyserName;
    }

    public SearcherLease AcquireSearcher()
    {
        _searcherLock.EnterReadLock();
        if (Volatile.Read(ref _disposed) != 0)
        {
            _searcherLock.ExitReadLock();
            throw new ObjectDisposedException(nameof(LeanCorpusIndexHandle));
        }

        return new SearcherLease(_searcherLock, _searcher);
    }

    public void CommitAndRefresh()
    {
        Writer.Commit();
        var replacement = new IndexSearcher(Directory);
        _searcherLock.EnterWriteLock();
        try
        {
            var previous = _searcher;
            _searcher = replacement;
            previous.Dispose();
        }
        catch
        {
            replacement.Dispose();
            throw;
        }
        finally
        {
            _searcherLock.ExitWriteLock();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _searcherLock.EnterWriteLock();
        try { _searcher.Dispose(); }
        finally { _searcherLock.ExitWriteLock(); }

        Writer.Dispose();
        Directory.Dispose();
        _searcherLock.Dispose();
    }
}

internal sealed class SearcherLease : IDisposable
{
    private ReaderWriterLockSlim? _lock;

    internal IndexSearcher Searcher { get; }

    public SearcherLease(ReaderWriterLockSlim @lock, IndexSearcher searcher)
    {
        _lock = @lock;
        Searcher = searcher;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _lock, null)?.ExitReadLock();
    }
}
