using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrchardCore.Indexing;
using OrchardCore.Indexing.Models;
using Rowles.LeanCorpus.OrchardCore.Search.Storage;
using Rowles.LeanCorpus.OrchardCore.Search.Models;
using Rowles.LeanCorpus.Search.Queries;
using Rowles.LeanCorpus.Search.Searcher;
using Rowles.LeanCorpus.Search.Scoring;

namespace Rowles.LeanCorpus.OrchardCore.Search.Indexing;

/// <summary>Maps Orchard document-index batches and commits them durably to LeanCorpus.</summary>
public sealed class LeanCorpusDocumentIndexManager : IDocumentIndexManager
{
    private readonly LeanCorpusIndexPathResolver _paths;
    private readonly LeanCorpusIndexHandleCache _handles;
    private readonly LeanCorpusSchemaStore _schemas;
    private readonly LeanCorpusDocumentMapper _mapper;
    private readonly ILeanCorpusFailureInjector _failures;
    private readonly LeanCorpusContentFieldRefresher? _contentFieldRefresher;
    private readonly ILogger<LeanCorpusDocumentIndexManager> _logger;

    public LeanCorpusDocumentIndexManager(IServiceProvider services)
        : this(
            services.GetRequiredService<LeanCorpusIndexPathResolver>(),
            services.GetRequiredService<LeanCorpusIndexHandleCache>(),
            services.GetRequiredService<LeanCorpusSchemaStore>(),
            services.GetRequiredService<LeanCorpusDocumentMapper>(),
            services.GetRequiredService<ILeanCorpusFailureInjector>(),
            services.GetRequiredService<LeanCorpusContentFieldRefresher>(),
            services.GetRequiredService<ILogger<LeanCorpusDocumentIndexManager>>())
    {
    }

    internal LeanCorpusDocumentIndexManager(
        LeanCorpusIndexPathResolver paths,
        LeanCorpusIndexHandleCache handles,
        LeanCorpusSchemaStore schemas,
        LeanCorpusDocumentMapper mapper,
        ILeanCorpusFailureInjector failures,
        ILogger<LeanCorpusDocumentIndexManager> logger)
        : this(paths, handles, schemas, mapper, failures, contentFieldRefresher: null, logger)
    {
    }

    internal LeanCorpusDocumentIndexManager(
        LeanCorpusIndexPathResolver paths,
        LeanCorpusIndexHandleCache handles,
        LeanCorpusSchemaStore schemas,
        LeanCorpusDocumentMapper mapper,
        ILeanCorpusFailureInjector failures,
        LeanCorpusContentFieldRefresher? contentFieldRefresher,
        ILogger<LeanCorpusDocumentIndexManager> logger)
    {
        _paths = paths;
        _handles = handles;
        _schemas = schemas;
        _mapper = mapper;
        _failures = failures;
        _contentFieldRefresher = contentFieldRefresher;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> AddOrUpdateDocumentsAsync(IndexProfile indexProfile, IEnumerable<DocumentIndex> documents)
    {
        DocumentIndex[] batch = documents?.ToArray() ?? [];
        if (batch.Length == 0)
            return false;

        LeanCorpusIndexPaths paths = _paths.Resolve(indexProfile.IndexFullName);
        return await _handles.WithExclusiveAsync(paths, async entry =>
        {
            try
            {
                DocumentIndex[] batchToMap = _contentFieldRefresher is null
                    ? batch
                    : await _contentFieldRefresher.RefreshAsync(batch, GetContentIndexSettings()).ConfigureAwait(false);
                _ = await LeanCorpusIndexingStateStore.ReadLastTaskIdAsync(paths.StatePath).ConfigureAwait(false);
                LeanCorpusSchemaManifest current = await _schemas.ReadAsync(paths.SchemaPath).ConfigureAwait(false);
                LeanCorpusIndexMetadata metadata = indexProfile.GetLeanCorpusMetadata(current.Metadata);
                if (current.Fields.Count > 0 && current.Metadata.DefaultAnalyser != metadata.DefaultAnalyser)
                    throw new InvalidDataException("Changing the default analyser requires a physical index rebuild.");
                if (!LeanCorpusIndexProfileMetadataExtensions.MetadataEquals(current.Metadata, metadata))
                {
                    bool analyserChanged = current.Metadata.DefaultAnalyser != metadata.DefaultAnalyser;
                    current = current with { Metadata = metadata };
                    await _schemas.PublishAsync(paths.SchemaPath, current).ConfigureAwait(false);
                    if (analyserChanged)
                        entry.Close();
                }
                LeanCorpusMappedBatch mapped = _mapper.Map(batchToMap, current);
                if (!SameSchema(current, mapped.Manifest))
                    await _schemas.PublishAsync(paths.SchemaPath, mapped.Manifest).ConfigureAwait(false);

                _failures.Check(LeanCorpusFailurePoint.BeforeDocumentWrite);
                LeanCorpusIndexHandle handle = entry.Open(createIfMissing: false, current);
                for (int index = 0; index < batchToMap.Length; index++)
                    handle.Writer.UpdateDocument(LeanCorpusDocumentMapper.DocumentIdField, batchToMap[index].Id, mapped.Documents[index]);

                _failures.Check(LeanCorpusFailurePoint.AfterDocumentWriteBeforeCommit);
                handle.CommitAndRefresh();
                _failures.Check(LeanCorpusFailurePoint.AfterCommitBeforeCursor);
                return true;
            }
            catch (InvalidDataException exception)
            {
                entry.Close();
                _logger.LogError(exception, "LeanCorpus cursor or schema state is invalid for index {IndexFullName}.", indexProfile.IndexFullName);
                return false;
            }
            catch (Exception exception)
            {
                entry.Close();
                _logger.LogError(exception, "LeanCorpus document batch failed for index {IndexFullName}.", indexProfile.IndexFullName);
                return false;
            }
        }).ConfigureAwait(false);
    }

    /// <summary>Adds prebuilt documents to an index immediately after a physical rebuild.</summary>
    /// <remarks>
    /// The caller must have rebuilt the index and must supply documents with unique IDs.
    /// This path skips update-by-ID deletion work and Orchard content refresh handlers, so it is
    /// intended only for complete, already-mapped rebuild batches. Normal Orchard task replay must
    /// continue to use <see cref="AddOrUpdateDocumentsAsync"/>.
    /// </remarks>
    internal async Task<bool> AddDocumentsToEmptyIndexAsync(IndexProfile indexProfile, IReadOnlyList<DocumentIndex> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count == 0)
            return false;
        if (documents.Any(document => document is ContentItemDocumentIndex))
            throw new ArgumentException("A full rebuild batch must contain prebuilt provider-neutral documents.", nameof(documents));

        LeanCorpusIndexPaths paths = _paths.Resolve(indexProfile.IndexFullName);
        return await _handles.WithExclusiveAsync(paths, async entry =>
        {
            try
            {
                _ = await LeanCorpusIndexingStateStore.ReadLastTaskIdAsync(paths.StatePath).ConfigureAwait(false);
                LeanCorpusSchemaManifest current = await _schemas.ReadAsync(paths.SchemaPath).ConfigureAwait(false);
                LeanCorpusIndexMetadata metadata = indexProfile.GetLeanCorpusMetadata(current.Metadata);
                if (current.Fields.Count > 0 && current.Metadata.DefaultAnalyser != metadata.DefaultAnalyser)
                    throw new InvalidDataException("Changing the default analyser requires a physical index rebuild.");
                if (!LeanCorpusIndexProfileMetadataExtensions.MetadataEquals(current.Metadata, metadata))
                {
                    bool analyserChanged = current.Metadata.DefaultAnalyser != metadata.DefaultAnalyser;
                    current = current with { Metadata = metadata };
                    await _schemas.PublishAsync(paths.SchemaPath, current).ConfigureAwait(false);
                    if (analyserChanged)
                        entry.Close();
                }

                LeanCorpusMappedBatch mapped = _mapper.Map(documents, current);
                if (!SameSchema(current, mapped.Manifest))
                    await _schemas.PublishAsync(paths.SchemaPath, mapped.Manifest).ConfigureAwait(false);

                _failures.Check(LeanCorpusFailurePoint.BeforeDocumentWrite);
                LeanCorpusIndexHandle handle = entry.Open(createIfMissing: false, mapped.Manifest);
                handle.Writer.AddDocuments(mapped.Documents);
                _failures.Check(LeanCorpusFailurePoint.AfterDocumentWriteBeforeCommit);
                handle.CommitAndRefresh();
                _failures.Check(LeanCorpusFailurePoint.AfterCommitBeforeCursor);
                return true;
            }
            catch (InvalidDataException exception)
            {
                entry.Close();
                _logger.LogError(exception, "LeanCorpus full-build batch failed for index {IndexFullName}.", indexProfile.IndexFullName);
                return false;
            }
            catch (Exception exception)
            {
                entry.Close();
                _logger.LogError(exception, "LeanCorpus full-build batch failed for index {IndexFullName}.", indexProfile.IndexFullName);
                return false;
            }
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteDocumentsAsync(IndexProfile indexProfile, IEnumerable<string> documentIds)
    {
        string[] ids = documentIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray() ?? [];
        if (ids.Length == 0)
            return false;

        LeanCorpusIndexPaths paths = _paths.Resolve(indexProfile.IndexFullName);
        return await _handles.WithExclusiveAsync(paths, async entry =>
        {
            try
            {
                _ = await LeanCorpusIndexingStateStore.ReadLastTaskIdAsync(paths.StatePath).ConfigureAwait(false);
                LeanCorpusIndexHandle handle = entry.Open(createIfMissing: false);
                foreach (string id in ids)
                    handle.Writer.DeleteDocuments(new TermQuery(LeanCorpusDocumentMapper.DocumentIdField, id));
                handle.CommitAndRefresh();
                _failures.Check(LeanCorpusFailurePoint.AfterDeleteCommitBeforeCursor);
                return true;
            }
            catch (InvalidDataException exception)
            {
                entry.Close();
                _logger.LogError(exception, "LeanCorpus cursor state is invalid for index {IndexFullName}.", indexProfile.IndexFullName);
                return false;
            }
            catch (Exception exception)
            {
                entry.Close();
                _logger.LogError(exception, "LeanCorpus document delete failed for index {IndexFullName}.", indexProfile.IndexFullName);
                return false;
            }
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAllDocumentsAsync(IndexProfile indexProfile)
    {
        LeanCorpusIndexPaths paths = _paths.Resolve(indexProfile.IndexFullName);
        return await _handles.WithExclusiveAsync(paths, async entry =>
        {
            try
            {
                _ = await LeanCorpusIndexingStateStore.ReadLastTaskIdAsync(paths.StatePath).ConfigureAwait(false);
                LeanCorpusIndexHandle handle = entry.Open(createIfMissing: false);
                DeleteAllDocuments(handle);
                return true;
            }
            catch (InvalidDataException exception)
            {
                entry.Close();
                _logger.LogError(exception, "LeanCorpus cursor state is invalid for index {IndexFullName}.", indexProfile.IndexFullName);
                return false;
            }
            catch (Exception exception)
            {
                entry.Close();
                _logger.LogError(exception, "LeanCorpus delete-all failed for index {IndexFullName}.", indexProfile.IndexFullName);
                return false;
            }
        }).ConfigureAwait(false);
    }

    internal async Task<bool> ResetAsync(IndexProfile indexProfile)
    {
        LeanCorpusIndexPaths paths = _paths.Resolve(indexProfile.IndexFullName);
        return await _handles.WithExclusiveAsync(paths, async entry =>
        {
            try
            {
                _ = await LeanCorpusIndexingStateStore.ReadLastTaskIdAsync(paths.StatePath).ConfigureAwait(false);
                LeanCorpusIndexHandle handle = entry.Open(createIfMissing: false);

                // Rewind before deleting. A crash from this point onwards makes Orchard replay tasks
                // from zero, so the old documents can be safely replaced or deleted again.
                await LeanCorpusIndexingStateStore.PublishAsync(paths.StatePath, 0, _failures).ConfigureAwait(false);
                DeleteAllDocuments(handle);
                return true;
            }
            catch (InvalidDataException exception)
            {
                entry.Close();
                _logger.LogError(exception, "LeanCorpus cursor state is invalid for reset of index {IndexFullName}.", indexProfile.IndexFullName);
                return false;
            }
            catch (Exception exception)
            {
                entry.Close();
                _logger.LogError(exception, "LeanCorpus reset failed for index {IndexFullName}.", indexProfile.IndexFullName);
                return false;
            }
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> GetLastTaskIdAsync(IndexProfile indexProfile)
    {
        LeanCorpusIndexPaths paths = _paths.Resolve(indexProfile.IndexFullName);
        return await _handles.WithExclusiveAsync(paths, async entry =>
        {
            try
            {
                return await LeanCorpusIndexingStateStore.ReadLastTaskIdAsync(paths.StatePath).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "LeanCorpus cursor state is invalid for index {IndexFullName}.", indexProfile.IndexFullName);
                throw;
            }
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetLastTaskIdAsync(IndexProfile indexProfile, long lastTaskId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lastTaskId);
        LeanCorpusIndexPaths paths = _paths.Resolve(indexProfile.IndexFullName);
        await _handles.WithExclusiveAsync(paths, async entry =>
        {
            try
            {
                _ = await LeanCorpusIndexingStateStore.ReadLastTaskIdAsync(paths.StatePath).ConfigureAwait(false);
                await LeanCorpusIndexingStateStore.PublishAsync(paths.StatePath, lastTaskId, _failures).ConfigureAwait(false);
                return true;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to publish LeanCorpus cursor for index {IndexFullName}.", indexProfile.IndexFullName);
                throw;
            }
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IContentIndexSettings GetContentIndexSettings() => new LeanCorpusContentIndexSettings();

    private static bool SameSchema(LeanCorpusSchemaManifest left, LeanCorpusSchemaManifest right)
        => left.Fields.Count == right.Fields.Count
            && left.Fields.All(pair => right.Fields.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static void DeleteAllDocuments(LeanCorpusIndexHandle handle)
    {
        string[] ids;
        using (var lease = handle.AcquireSearcher())
        {
            TopDocs hits = lease.Searcher.Search(new MatchAllDocsQuery(), int.MaxValue);
            var documentIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var hit in hits.ScoreDocs)
            {
                var fields = lease.Searcher.GetStoredFields(hit.DocId);
                if (fields.TryGetValue(LeanCorpusDocumentMapper.DocumentIdField, out var values))
                    foreach (string id in values)
                        documentIds.Add(id);
            }

            ids = documentIds.ToArray();
        }

        foreach (string id in ids)
            handle.Writer.DeleteDocuments(new TermQuery(LeanCorpusDocumentMapper.DocumentIdField, id));
        handle.CommitAndRefresh();
    }
}
