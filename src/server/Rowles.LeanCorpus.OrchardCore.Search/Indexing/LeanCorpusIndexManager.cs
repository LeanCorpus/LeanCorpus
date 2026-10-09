using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using OrchardCore.Locking;
using OrchardCore.Locking.Distributed;
using OrchardCore.Indexing;
using OrchardCore.Indexing.Models;
using Rowles.LeanCorpus.OrchardCore.Search.Storage;
using Rowles.LeanCorpus.OrchardCore.Search.Models;
using Rowles.LeanCorpus.Index.Indexer;
using Rowles.LeanCorpus.Store;

namespace Rowles.LeanCorpus.OrchardCore.Search.Indexing;

/// <summary>Manages tenant-local LeanCorpus index creation, replacement and deletion.</summary>
public sealed class LeanCorpusIndexManager : IIndexManager
{
    private readonly LeanCorpusIndexPathResolver _paths;
    private readonly LeanCorpusIndexHandleCache _handles;
    private readonly LeanCorpusSchemaStore _schemas;
    private readonly ILeanCorpusFailureInjector _failures;
    private readonly ILogger<LeanCorpusIndexManager> _logger;
    private readonly IDistributedLock? _distributedLock;

    public LeanCorpusIndexManager(IServiceProvider services)
        : this(
            services.GetRequiredService<LeanCorpusIndexPathResolver>(),
            services.GetRequiredService<LeanCorpusIndexHandleCache>(),
            services.GetRequiredService<LeanCorpusSchemaStore>(),
            services.GetRequiredService<ILeanCorpusFailureInjector>(),
            services.GetRequiredService<ILogger<LeanCorpusIndexManager>>(),
            services.GetRequiredService<IDistributedLock>())
    {
    }

    internal LeanCorpusIndexManager(
        LeanCorpusIndexPathResolver paths,
        LeanCorpusIndexHandleCache handles,
        LeanCorpusSchemaStore schemas,
        ILeanCorpusFailureInjector failures,
        ILogger<LeanCorpusIndexManager> logger,
        IDistributedLock? distributedLock = null)
    {
        _paths = paths;
        _handles = handles;
        _schemas = schemas;
        _failures = failures;
        _logger = logger;
        _distributedLock = distributedLock;
    }

    /// <inheritdoc />
    public async Task<bool> CreateAsync(IndexProfile indexProfile)
    {
        var paths = _paths.Resolve(indexProfile.IndexFullName);
        return await _handles.WithExclusiveAsync(paths, async entry =>
        {
            if (Directory.Exists(paths.IndexDirectory))
                return false;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(paths.IndexDirectory)!);
                Directory.CreateDirectory(Path.GetDirectoryName(paths.SchemaPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(paths.StatePath)!);
                var initialSchema = new LeanCorpusSchemaManifest { Metadata = indexProfile.GetLeanCorpusMetadata() };
                await _schemas.PublishAsync(paths.SchemaPath, initialSchema).ConfigureAwait(false);
                await LeanCorpusIndexingStateStore.PublishAsync(paths.StatePath, 0, _failures).ConfigureAwait(false);
                entry.Open(createIfMissing: true, initialSchema);
                return true;
            }
            catch (Exception exception)
            {
                entry.Close();
                DeleteIfPresent(paths.IndexDirectory);
                DeleteIfPresent(paths.SchemaPath);
                DeleteIfPresent(paths.StatePath);
                _logger.LogError(exception, "Failed to create LeanCorpus index {IndexFullName}.", indexProfile.IndexFullName);
                return false;
            }
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string indexFullName)
    {
        LeanCorpusIndexPaths paths = _paths.Resolve(indexFullName);
        if (!HasCommit(paths.IndexDirectory) || !File.Exists(paths.SchemaPath))
            return false;

        try
        {
            LeanCorpusSchemaManifest schema = await _schemas.ReadAsync(paths.SchemaPath).ConfigureAwait(false);
            return await _handles.WithSearcherAsync(paths, _ => true, schema).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to open LeanCorpus index {IndexFullName}.", indexFullName);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(IndexProfile indexProfile)
    {
        LeanCorpusIndexPaths paths = _paths.Resolve(indexProfile.IndexFullName);
        return await _handles.WithExclusiveAsync(paths, entry =>
        {
            try
            {
                entry.Close();
                DeleteIfPresent(paths.IndexDirectory);
                DeleteIfPresent(paths.IndexDirectory + ".rebuild");
                DeleteIfPresent(paths.IndexDirectory + ".replaced");
                File.Delete(paths.SchemaPath);
                File.Delete(paths.StatePath);
                return Task.FromResult(true);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to delete LeanCorpus index {IndexFullName}.", indexProfile.IndexFullName);
                return Task.FromResult(false);
            }
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RebuildAsync(IndexProfile indexProfile)
    {
        ILocker? locker = null;
        if (_distributedLock is not null)
        {
            (ILocker acquiredLocker, bool acquired) = await _distributedLock.TryAcquireLockAsync(
                $"LeanCorpusRebuild-{indexProfile.Id}", TimeSpan.Zero, expiration: null).ConfigureAwait(false);
            if (!acquired)
            {
                _logger.LogWarning("Could not acquire the Orchard distributed lock for LeanCorpus index rebuild {IndexFullName}.", indexProfile.IndexFullName);
                return false;
            }
            locker = acquiredLocker;
        }

        using (locker)
            return await RebuildCoreAsync(indexProfile).ConfigureAwait(false);
    }

    private async Task<bool> RebuildCoreAsync(IndexProfile indexProfile)
    {
        LeanCorpusIndexPaths paths = _paths.Resolve(indexProfile.IndexFullName);
        return await _handles.WithExclusiveAsync(paths, async entry =>
        {
            string replacementPath = paths.IndexDirectory + ".rebuild";
            string previousPath = paths.IndexDirectory + ".replaced";
            try
            {
                LeanCorpusIndexMetadata storedMetadata = File.Exists(paths.SchemaPath)
                    ? (await _schemas.ReadAsync(paths.SchemaPath).ConfigureAwait(false)).Metadata
                    : new LeanCorpusIndexMetadata();
                LeanCorpusIndexMetadata metadata = indexProfile.GetLeanCorpusMetadata(storedMetadata);
                var replacementSchema = new LeanCorpusSchemaManifest { Metadata = metadata };
                entry.Close();
                if (!Directory.Exists(paths.IndexDirectory) && Directory.Exists(previousPath))
                    Directory.Move(previousPath, paths.IndexDirectory);
                else
                    DeleteIfPresent(previousPath);

                DeleteIfPresent(replacementPath);
                Directory.CreateDirectory(Path.GetDirectoryName(paths.IndexDirectory)!);
                using (var directory = new MMapDirectory(replacementPath))
                using (var writer = new IndexWriter(directory, new IndexWriterConfig
                {
                    DurableCommits = true,
                    DefaultAnalyser = metadata.DefaultAnalyser switch
                    {
                        "standard" => new Rowles.LeanCorpus.Analysis.Analysers.StandardAnalyser(),
                        "keyword" => new Rowles.LeanCorpus.Analysis.Analysers.KeywordAnalyser(),
                        _ => throw new InvalidDataException("The LeanCorpus index metadata names an unsupported analyser."),
                    },
                }))
                    writer.Commit();

                // A stale cursor is safe: if publication fails, Orchard replays from zero.
                await LeanCorpusIndexingStateStore.PublishAsync(paths.StatePath, 0, _failures).ConfigureAwait(false);
                if (Directory.Exists(paths.IndexDirectory))
                    Directory.Move(paths.IndexDirectory, previousPath);
                try
                {
                    Directory.Move(replacementPath, paths.IndexDirectory);
                }
                catch
                {
                    if (!Directory.Exists(paths.IndexDirectory) && Directory.Exists(previousPath))
                        Directory.Move(previousPath, paths.IndexDirectory);
                    throw;
                }

                await _schemas.PublishAsync(paths.SchemaPath, replacementSchema).ConfigureAwait(false);
                DeleteIfPresent(previousPath);
                return true;
            }
            catch (Exception exception)
            {
                entry.Close();
                DeleteIfPresent(replacementPath);
                _logger.LogError(exception, "Failed to rebuild LeanCorpus index {IndexFullName}.", indexProfile.IndexFullName);
                return false;
            }
        }).ConfigureAwait(false);
    }

    private static bool HasCommit(string path)
        => Directory.Exists(path) && Directory.EnumerateFiles(path, "segments_*").Any();

    private static void DeleteIfPresent(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        else
            File.Delete(path);
    }
}
