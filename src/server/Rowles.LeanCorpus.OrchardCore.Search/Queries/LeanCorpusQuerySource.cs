using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrchardCore.Indexing;
using OrchardCore.Queries;
using Rowles.LeanCorpus.OrchardCore.Search.Indexing;
using Rowles.LeanCorpus.OrchardCore.Search.Storage;

namespace Rowles.LeanCorpus.OrchardCore.Search.Queries;

/// <summary>Executes bounded native LeanCorpus query definitions.</summary>
public sealed class LeanCorpusQuerySource : IQuerySource
{
    private readonly LeanCorpusIndexNameProvider _nameProvider;
    private readonly LeanCorpusIndexPathResolver _paths;
    private readonly LeanCorpusIndexHandleCache _handles;
    private readonly LeanCorpusSchemaStore _schemas;
    private readonly LeanCorpusQueryCompiler _compiler;
    private readonly ILogger<LeanCorpusQuerySource> _logger;

    public LeanCorpusQuerySource(IServiceProvider services)
        : this(
            services.GetRequiredService<LeanCorpusIndexNameProvider>(),
            services.GetRequiredService<LeanCorpusIndexPathResolver>(),
            services.GetRequiredService<LeanCorpusIndexHandleCache>(),
            services.GetRequiredService<LeanCorpusSchemaStore>(),
            services.GetRequiredService<LeanCorpusQueryCompiler>(),
            services.GetRequiredService<ILogger<LeanCorpusQuerySource>>())
    {
    }

    internal LeanCorpusQuerySource(
        LeanCorpusIndexNameProvider nameProvider,
        LeanCorpusIndexPathResolver paths,
        LeanCorpusIndexHandleCache handles,
        LeanCorpusSchemaStore schemas,
        LeanCorpusQueryCompiler compiler,
        ILogger<LeanCorpusQuerySource> logger)
    {
        _nameProvider = nameProvider;
        _paths = paths;
        _handles = handles;
        _schemas = schemas;
        _compiler = compiler;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "LeanCorpus";

    /// <inheritdoc />
    public async Task<IQueryResults> ExecuteQueryAsync(Query query, IDictionary<string, object> parameters)
    {
        ArgumentNullException.ThrowIfNull(query);
        _ = parameters;
        try
        {
            if (string.IsNullOrWhiteSpace(query.Schema))
                throw new InvalidDataException("A LeanCorpus stored query must contain its native JSON in the schema field.");

            using var json = System.Text.Json.JsonDocument.Parse(query.Schema,
                new System.Text.Json.JsonDocumentOptions { MaxDepth = LeanCorpusQueryCompiler.MaximumDepth });
            if (!json.RootElement.TryGetProperty("index", out var indexElement)
                || indexElement.ValueKind != System.Text.Json.JsonValueKind.String
                || string.IsNullOrWhiteSpace(indexElement.GetString()))
                throw new InvalidDataException("A LeanCorpus stored query requires an index name.");

            string indexFullName = _nameProvider.GetFullIndexName(indexElement.GetString()!);
            LeanCorpusIndexPaths paths = _paths.Resolve(indexFullName);
            var items = await _handles.WithSearcherAsync(paths, (searcher, schema) =>
            {
                var (_, compiled) = _compiler.Compile(query.Schema, schema);
                int topN = compiled is Rowles.LeanCorpus.Search.Queries.VectorQuery vector
                    ? vector.TopK
                    : int.MaxValue;
                var hits = searcher.Search(compiled, topN);
                return hits.ScoreDocs
                    .Skip(ParseSkip(json.RootElement))
                    .Take(ParseTake(json.RootElement))
                    .Select(hit =>
                    {
                        var stored = searcher.GetStoredFields(hit.DocId);
                        string id = ReadFirst(stored, LeanCorpusDocumentMapper.ContentItemIdField)
                            ?? ReadFirst(stored, LeanCorpusDocumentMapper.DocumentIdField)
                            ?? string.Empty;
                        return new LeanCorpusQueryResultItem(id, stored);
                    })
                    .Where(item => item.Id.Length > 0)
                    .Cast<object>()
                    .ToArray();
            }, _ => _schemas.ReadAsync(paths.SchemaPath)).ConfigureAwait(false);
            return new LeanCorpusQueryResults(items);
        }
        catch (InvalidDataException exception)
        {
            _logger.LogWarning(exception, "LeanCorpus stored query is invalid.");
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "LeanCorpus stored query execution failed.");
            throw;
        }
    }

    private static int ParseSkip(System.Text.Json.JsonElement root)
        => root.TryGetProperty("skip", out var value) && value.TryGetInt32(out int skip) ? Math.Clamp(skip, 0, 1_000_000) : 0;

    private static int ParseTake(System.Text.Json.JsonElement root)
        => root.TryGetProperty("take", out var value) && value.TryGetInt32(out int take)
            ? Math.Clamp(take, 0, LeanCorpusQueryCompiler.MaximumTake)
            : 20;

    private static string? ReadFirst(IReadOnlyDictionary<string, IReadOnlyList<string>> fields, string name)
        => fields.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;
}

internal sealed record LeanCorpusQueryResultItem(
    string Id,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Fields);

internal sealed class LeanCorpusQueryResults(IEnumerable<object> items) : IQueryResults
{
    public IEnumerable<object> Items { get; set; } = items;
}
