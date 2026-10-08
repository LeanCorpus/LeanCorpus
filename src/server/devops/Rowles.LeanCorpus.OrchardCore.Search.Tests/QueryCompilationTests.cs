using OrchardCore.Indexing;
using Microsoft.Extensions.Logging.Abstractions;
using OrchardQuery = global::OrchardCore.Queries.Query;
using OrchardQueryResults = global::OrchardCore.Queries.IQueryResults;
using Rowles.LeanCorpus.OrchardCore.Search.Indexing;
using Rowles.LeanCorpus.OrchardCore.Search.Models;
using Rowles.LeanCorpus.OrchardCore.Search.Queries;
using Rowles.LeanCorpus.OrchardCore.Search.Search;
using Rowles.LeanCorpus.OrchardCore.Search.Storage;
using Rowles.LeanCorpus.Search;
using Rowles.LeanCorpus.Search.Queries;

namespace Rowles.LeanCorpus.OrchardCore.Search.Tests;

[Trait("Area", "Orchard")]
public sealed class QueryCompilationTests
{
    private readonly LeanCorpusDocumentMapper _mapper = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<LeanCorpusDocumentMapper>.Instance);
    private readonly LeanCorpusQueryCompiler _compiler = new();

    [Fact]
    public void GenericSearchRequiresEveryTokenAndAllowsEachTokenAcrossFields()
    {
        var document = new DocumentIndex("one");
        document.Set("Title", "green", DocumentIndexOptions.None);
        document.Set("Body", "field guide", DocumentIndexOptions.None);
        LeanCorpusSchemaManifest schema = _mapper.Map([document], new LeanCorpusSchemaManifest()).Manifest;

        Query compiled = new LeanCorpusSearchCompiler().Compile("green guide",
            new LeanCorpusIndexMetadata { SearchFields = ["Title", "Body"] }, schema)!;
        var groups = Assert.IsType<BooleanQuery>(compiled);
        Assert.Equal(2, groups.Clauses.Count);
        Assert.All(groups.Clauses, clause => Assert.Equal(Occur.Must, clause.Occur));
        Assert.All(groups.Clauses, clause =>
        {
            var acrossFields = Assert.IsType<BooleanQuery>(clause.Query);
            Assert.Equal(2, acrossFields.Clauses.Count);
            Assert.All(acrossFields.Clauses, fieldClause => Assert.Equal(Occur.Should, fieldClause.Occur));
        });
    }

    [Fact]
    public void NativeQueryCompilerAcceptsBoundedOperatorsAndRejectsUnknownOnes()
    {
        var document = new DocumentIndex("one");
        document.Set("Title", "green field", DocumentIndexOptions.None);
        document.Set("Category", "garden", DocumentIndexOptions.Keyword);
        document.Set("Sequence", 17, DocumentIndexOptions.None);
        document.Set("PublishedUtc", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), DocumentIndexOptions.None);
        LeanCorpusSchemaManifest schema = _mapper.Map([document], new LeanCorpusSchemaManifest()).Manifest;

        var compiled = _compiler.Compile("""
            {"index":"Search","query":{"type":"and","queries":[
              {"type":"term","field":"Category","value":"garden"},
              {"type":"range","field":"Sequence","gte":10,"lte":20},
              {"type":"exists","field":"Title"},
              {"type":"isNull","field":"Title"}
            ]},"skip":2,"take":7}
            """, schema);

        Assert.Equal("Search", compiled.Request.Index);
        Assert.Equal(2, compiled.Request.Skip);
        Assert.Equal(7, compiled.Request.Take);
        var boolean = Assert.IsType<BooleanQuery>(compiled.Compiled);
        Assert.Equal(4, boolean.Clauses.Count);

        Assert.Throws<InvalidDataException>(() => _compiler.Compile(
            "{\"index\":\"Search\",\"query\":{\"type\":\"wildcard\",\"field\":\"Title\",\"value\":\"g*\"}}", schema));
        Assert.Throws<InvalidDataException>(() => _compiler.Compile(
            "{\"index\":\"Search\",\"query\":{\"type\":\"range\",\"field\":\"Title\",\"gte\":\"a\"}}", schema));
    }

    [Fact]
    public void NativeQueryCompilerValidatesVectorDimensionsAndFieldExistence()
    {
        var document = new DocumentIndex("one");
        document.Entries.Add(new DocumentIndex.DocumentIndexEntry("Embedding", new[] { 1f, 0f, 0f },
            DocumentIndex.Types.Vector, DocumentIndexOptions.None) { Dimensions = 3 });
        LeanCorpusSchemaManifest schema = _mapper.Map([document], new LeanCorpusSchemaManifest()).Manifest;

        var vector = _compiler.Compile("""
            {"index":"Search","query":{"type":"vectorKnn","field":"Embedding","value":[0.9,0.1,0.0],"topK":3}}
            """, schema);
        Assert.IsType<VectorQuery>(vector.Compiled);
        Assert.Throws<InvalidDataException>(() => _compiler.Compile(
            "{\"index\":\"Search\",\"query\":{\"type\":\"vectorKnn\",\"field\":\"Embedding\",\"value\":[1,0],\"topK\":3}}", schema));
        Assert.Throws<InvalidDataException>(() => _compiler.Compile(
            "{\"index\":\"Search\",\"query\":{\"type\":\"term\",\"field\":\"Unknown\",\"value\":\"x\"}}", schema));
    }

    [Fact]
    public async Task NativeGeoAndVectorQueriesUseRealLeanCorpusFields()
    {
        await using var fixture = new ProviderFixture();
        Assert.True(await fixture.Indexes.CreateAsync(fixture.Profile));

        var near = new DocumentIndex("geo-near");
        near.Set("Title", "near location", DocumentIndexOptions.None);
        near.Set("Sequence", 42, DocumentIndexOptions.None);
        near.Set("Rating", 4.75d, DocumentIndexOptions.None);
        near.Set("PublishedUtc", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), DocumentIndexOptions.None);
        near.Entries.Add(new DocumentIndex.DocumentIndexEntry("Location",
            new DocumentIndex.GeoPoint { Latitude = 51.5m, Longitude = -0.1m },
            DocumentIndex.Types.GeoPoint, DocumentIndexOptions.None));

        var far = new DocumentIndex("geo-far");
        far.Set("Title", "far location", DocumentIndexOptions.None);
        far.Set("Sequence", 5, DocumentIndexOptions.None);
        far.Set("Rating", 2.5d, DocumentIndexOptions.None);
        far.Set("PublishedUtc", new DateTimeOffset(2026, 1, 3, 3, 4, 5, TimeSpan.Zero), DocumentIndexOptions.None);
        far.Entries.Add(new DocumentIndex.DocumentIndexEntry("Location",
            new DocumentIndex.GeoPoint { Latitude = 40.7m, Longitude = -74m },
            DocumentIndex.Types.GeoPoint, DocumentIndexOptions.None));

        var closest = new DocumentIndex("vector-nearest");
        closest.Entries.Add(new DocumentIndex.DocumentIndexEntry("Embedding", new[] { 0.95f, 0.05f, 0f },
            DocumentIndex.Types.Vector, DocumentIndexOptions.None) { Dimensions = 3 });
        var distant = new DocumentIndex("vector-distant");
        distant.Entries.Add(new DocumentIndex.DocumentIndexEntry("Embedding", new[] { 0f, 1f, 0f },
            DocumentIndex.Types.Vector, DocumentIndexOptions.None) { Dimensions = 3 });

        Assert.True(await fixture.Documents.AddOrUpdateDocumentsAsync(fixture.Profile, [near, far, closest, distant]));
        LeanCorpusSchemaManifest schema = await fixture.Schemas.ReadAsync(
            fixture.Paths.Resolve(fixture.Profile.IndexFullName).SchemaPath, TestContext.Current.CancellationToken);

        var geo = _compiler.Compile("""
            {"index":"Search","query":{"type":"geoDistance","field":"Location","latitude":51.5,"longitude":-0.1,"radiusMetres":1000}}
            """, schema);
        Assert.Equal(new[] { "geo-near" }, await fixture.SearchCompiledAsync(geo.Compiled, 10));

        var vector = _compiler.Compile("""
            {"index":"Search","query":{"type":"vectorKnn","field":"Embedding","value":[1,0,0],"topK":2}}
            """, schema);
        string[] vectorResults = await fixture.SearchCompiledAsync(vector.Compiled, 2);
        Assert.Equal("vector-nearest", vectorResults[0]);

        var integerRange = _compiler.Compile("""
            {"index":"Search","query":{"type":"range","field":"Sequence","gte":10,"lte":50}}
            """, schema);
        Assert.Equal(new[] { "geo-near" }, await fixture.SearchCompiledAsync(integerRange.Compiled, 10));

        var numberRange = _compiler.Compile("""
            {"index":"Search","query":{"type":"range","field":"Rating","gte":4,"lte":5}}
            """, schema);
        Assert.Equal(new[] { "geo-near" }, await fixture.SearchCompiledAsync(numberRange.Compiled, 10));

        var dateRange = _compiler.Compile("""
            {"index":"Search","query":{"type":"range","field":"PublishedUtc","gte":"2026-01-02T03:04:05Z","lte":"2026-01-02T03:04:05Z"}}
            """, schema);
        Assert.Equal(new[] { "geo-near" }, await fixture.SearchCompiledAsync(dateRange.Compiled, 10));
    }

    [Fact]
    public async Task MultiValueKeywordAndNullFieldsRemainQueryable()
    {
        await using var fixture = new ProviderFixture();
        Assert.True(await fixture.Indexes.CreateAsync(fixture.Profile));

        var article = new DocumentIndex("field-semantics");
        article.Entries.Add(new DocumentIndex.DocumentIndexEntry("Tags", new[] { "green", "garden" },
            DocumentIndex.Types.Text, DocumentIndexOptions.Keyword | DocumentIndexOptions.Store));
        article.Entries.Add(new DocumentIndex.DocumentIndexEntry("Optional", null,
            DocumentIndex.Types.Text, DocumentIndexOptions.Store));
        Assert.True(await fixture.Documents.AddOrUpdateDocumentsAsync(fixture.Profile, [article]));

        LeanCorpusSchemaManifest schema = await fixture.Schemas.ReadAsync(
            fixture.Paths.Resolve(fixture.Profile.IndexFullName).SchemaPath, TestContext.Current.CancellationToken);
        Assert.True(schema.Fields["Tags"].MultiValued);
        Assert.True(schema.Fields["Tags"].Keyword);

        var keyword = _compiler.Compile("""
            {"index":"Search","query":{"type":"term","field":"Tags","value":"garden"}}
            """, schema);
        var isNull = _compiler.Compile("""
            {"index":"Search","query":{"type":"isNull","field":"Optional"}}
            """, schema);
        var exists = _compiler.Compile("""
            {"index":"Search","query":{"type":"exists","field":"Optional"}}
            """, schema);

        Assert.Equal(new[] { "field-semantics" }, await fixture.SearchCompiledAsync(keyword.Compiled, 10));
        Assert.Equal(new[] { "field-semantics" }, await fixture.SearchCompiledAsync(isNull.Compiled, 10));
        Assert.Equal(new[] { "field-semantics" }, await fixture.SearchCompiledAsync(exists.Compiled, 10));
    }

    [Fact]
    public async Task NativeQuerySourceReturnsContentIdsAndStoredFields()
    {
        await using var fixture = new ProviderFixture();
        Assert.True(await fixture.Indexes.CreateAsync(fixture.Profile));
        var article = new DocumentIndex("query-source-item");
        article.Set("Title", "Stored query source result", DocumentIndexOptions.Store);
        article.Set("Category", "guides", DocumentIndexOptions.Keyword | DocumentIndexOptions.Store);
        Assert.True(await fixture.Documents.AddOrUpdateDocumentsAsync(fixture.Profile, [article]));

        var source = new LeanCorpusQuerySource(fixture.Names, fixture.Paths, GetHandles(fixture), fixture.Schemas,
            _compiler, NullLogger<LeanCorpusQuerySource>.Instance);
        var query = new OrchardQuery
        {
            Source = "LeanCorpus",
            Schema = """
                {"index":"Search","query":{"type":"term","field":"Category","value":"guides"}}
                """,
        };

        OrchardQueryResults results = await source.ExecuteQueryAsync(query, new Dictionary<string, object>());
        LeanCorpusQueryResultItem result = Assert.IsType<LeanCorpusQueryResultItem>(Assert.Single(results.Items));
        Assert.Equal("query-source-item", result.Id);
        Assert.Equal("Stored query source result", Assert.Single(result.Fields["Title"]));
        Assert.Equal("guides", Assert.Single(result.Fields["Category"]));
    }

    private static LeanCorpusIndexHandleCache GetHandles(ProviderFixture fixture)
        => fixture.Handles;
}
