using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrchardCore.Indexing.Models;
using OrchardCore.Search.Abstractions;
using Rowles.LeanCorpus.OrchardCore.Search.Indexing;
using Rowles.LeanCorpus.OrchardCore.Search.Storage;
using Rowles.LeanCorpus.OrchardCore.Search.Models;
using Rowles.LeanCorpus.Search.Scoring;
using Rowles.LeanCorpus.Search.Searcher;

namespace Rowles.LeanCorpus.OrchardCore.Search.Search;

/// <summary>Provides Orchard's standard lexical site-search path over LeanCorpus.</summary>
public sealed class LeanCorpusSearchService : ISearchService
{
    private const int MaximumPageSize = 100;
    private const int SearchPageSize = 256;
    private readonly LeanCorpusIndexPathResolver _paths;
    private readonly LeanCorpusIndexHandleCache _handles;
    private readonly LeanCorpusSchemaStore _schemas;
    private readonly LeanCorpusSearchCompiler _compiler;
    private readonly ILogger<LeanCorpusSearchService> _logger;

    public LeanCorpusSearchService(IServiceProvider services)
        : this(
            services.GetRequiredService<LeanCorpusIndexPathResolver>(),
            services.GetRequiredService<LeanCorpusIndexHandleCache>(),
            services.GetRequiredService<LeanCorpusSchemaStore>(),
            services.GetRequiredService<LeanCorpusSearchCompiler>(),
            services.GetRequiredService<ILogger<LeanCorpusSearchService>>())
    {
    }

    internal LeanCorpusSearchService(
        LeanCorpusIndexPathResolver paths,
        LeanCorpusIndexHandleCache handles,
        LeanCorpusSchemaStore schemas,
        LeanCorpusSearchCompiler compiler,
        ILogger<LeanCorpusSearchService> logger)
    {
        _paths = paths;
        _handles = handles;
        _schemas = schemas;
        _compiler = compiler;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "LeanCorpus";

    /// <inheritdoc />
    public async Task<SearchResult> SearchAsync(IndexProfile index, string term, int start, int size)
    {
        if (string.IsNullOrWhiteSpace(term))
            return Empty(success: true);

        LeanCorpusIndexPaths paths = _paths.Resolve(index.IndexFullName);
        try
        {
            return await _handles.WithSearcherAsync(paths, (searcher, schema) =>
            {
                LeanCorpusIndexMetadata metadata = schema.Metadata;
                Rowles.LeanCorpus.Search.Query? query = _compiler.Compile(term, metadata, schema);
                if (query is null)
                    return Empty(success: true);

                int safeStart = Math.Max(0, start);
                int safeSize = Math.Clamp(size, 0, MaximumPageSize);
                SortField[] sorts = [SortField.Score, SortField.String(LeanCorpusDocumentMapper.DocumentIdField)];
                var seenContentIds = new HashSet<string>(StringComparer.Ordinal);
                var pageIds = new List<string>(safeSize);
                int uniqueResultIndex = 0;
                TopDocs hits = searcher.Search(query, SearchPageSize, sorts);
                while (true)
                {
                    foreach (var hit in hits.ScoreDocs)
                    {
                        var stored = searcher.GetStoredFields(hit.DocId);
                        string? documentId = ReadFirst(stored, LeanCorpusDocumentMapper.DocumentIdField);
                        string? contentId = ReadFirst(stored, LeanCorpusDocumentMapper.ContentItemIdField) ?? documentId;
                        if (string.IsNullOrWhiteSpace(contentId) || !seenContentIds.Add(contentId))
                            continue;

                        if (uniqueResultIndex >= safeStart && pageIds.Count < safeSize)
                            pageIds.Add(contentId);
                        uniqueResultIndex++;
                    }

                    if (hits.ScoreDocs.Length < SearchPageSize)
                        break;

                    SearchAfterValue[] cursor = searcher.CaptureSortValues(hits.ScoreDocs[^1], sorts);
                    hits = searcher.SearchAfter(cursor, query, SearchPageSize, sorts);
                }

                return new SearchResult
                {
                    Success = true,
                    Latest = true,
                    TotalCount = seenContentIds.Count,
                    ContentItemIds = pageIds,
                    Highlights = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>>(StringComparer.Ordinal),
                };
            }, async entry =>
            {
                LeanCorpusSchemaManifest schema = await _schemas.ReadAsync(paths.SchemaPath).ConfigureAwait(false);
                LeanCorpusIndexMetadata metadata = index.GetLeanCorpusMetadata(schema.Metadata);
                if (schema.Fields.Count > 0 && schema.Metadata.DefaultAnalyser != metadata.DefaultAnalyser)
                    throw new InvalidDataException("The index analyser changed and requires a physical rebuild before search.");

                bool analyserChanged = schema.Metadata.DefaultAnalyser != metadata.DefaultAnalyser;
                if (!LeanCorpusIndexProfileMetadataExtensions.MetadataEquals(schema.Metadata, metadata))
                {
                    schema = schema with { Metadata = metadata };
                    await _schemas.PublishAsync(paths.SchemaPath, schema).ConfigureAwait(false);
                    if (analyserChanged)
                        entry.Close();
                }

                return schema;
            }).ConfigureAwait(false);
        }
        catch (DirectoryNotFoundException exception)
        {
            _logger.LogWarning(exception, "LeanCorpus search index {IndexFullName} is missing.", index.IndexFullName);
            return Empty(success: false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "LeanCorpus search failed for index {IndexFullName}.", index.IndexFullName);
            return Empty(success: false);
        }
    }

    private static string? ReadFirst(
        IReadOnlyDictionary<string, IReadOnlyList<string>> fields,
        string name)
        => fields.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

    private static SearchResult Empty(bool success)
        => new()
        {
            Success = success,
            Latest = true,
            TotalCount = 0,
            ContentItemIds = [],
            Highlights = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>>(StringComparer.Ordinal),
        };
}
