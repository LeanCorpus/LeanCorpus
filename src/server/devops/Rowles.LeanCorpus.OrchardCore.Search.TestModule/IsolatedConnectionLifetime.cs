using System.Collections.Concurrent;
using System.Data.Common;
using YesSql;

namespace Rowles.LeanCorpus.OrchardCore.Search.TestModule;

/// <summary>Releases connections left open by Orchard's private indexing sessions when an isolated tenant shuts down.</summary>
internal sealed class IsolatedConnectionLifetime : IConnectionFactory, IDisposable, IAsyncDisposable
{
    private readonly ConcurrentQueue<WeakReference<DbConnection>> _connections = new();
    private IConnectionFactory _inner = null!;

    public Type DbConnectionType => _inner.DbConnectionType;

    public void Attach(IConnectionFactory inner) => _inner = inner;

    public DbConnection CreateConnection()
    {
        DbConnection connection = _inner.CreateConnection();
        _connections.Enqueue(new WeakReference<DbConnection>(connection));
        return connection;
    }

    public void Dispose()
    {
        // Orchard also disposes released tenant containers synchronously.
        while (_connections.TryDequeue(out WeakReference<DbConnection>? reference))
        {
            if (reference.TryGetTarget(out DbConnection? connection))
                connection.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // The host stops accepting requests before tenant disposal. Weak references avoid
        // retaining connections that their normal session owners already released.
        while (_connections.TryDequeue(out WeakReference<DbConnection>? reference))
        {
            if (reference.TryGetTarget(out DbConnection? connection))
                await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
