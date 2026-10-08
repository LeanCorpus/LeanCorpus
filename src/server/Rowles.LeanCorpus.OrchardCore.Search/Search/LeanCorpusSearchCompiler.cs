using Rowles.LeanCorpus.Analysis;
using Rowles.LeanCorpus.Analysis.Analysers;
using Rowles.LeanCorpus.OrchardCore.Search.Indexing;
using Rowles.LeanCorpus.OrchardCore.Search.Models;
using Rowles.LeanCorpus.Search;
using Rowles.LeanCorpus.Search.Queries;

namespace Rowles.LeanCorpus.OrchardCore.Search.Search;

internal sealed class LeanCorpusSearchCompiler
{
    public const int MaximumQueryLength = 4096;
    public const int MaximumTokenCount = 64;

    public Query? Compile(string queryText, LeanCorpusIndexMetadata metadata, LeanCorpusSchemaManifest schema)
    {
        ArgumentNullException.ThrowIfNull(queryText);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(schema);
        if (queryText.Length > MaximumQueryLength)
            throw new ArgumentException($"Search text may not exceed {MaximumQueryLength} characters.", nameof(queryText));

        string[] fields = ResolveFields(metadata, schema);
        if (fields.Length == 0)
            return null;

        IAnalyser analyser = metadata.DefaultAnalyser switch
        {
            "standard" => new StandardAnalyser(),
            "keyword" => new KeywordAnalyser(),
            _ => throw new InvalidDataException("The LeanCorpus index metadata names an unsupported analyser."),
        };
        var sink = new MaterialisedTokenSink();
        analyser.Analyse(queryText.AsSpan(), sink);
        string[] tokens = sink.Tokens.Distinct(StringComparer.Ordinal).Take(MaximumTokenCount + 1).ToArray();
        if (tokens.Length > MaximumTokenCount)
            throw new ArgumentException($"Search text may produce at most {MaximumTokenCount} tokens.", nameof(queryText));
        if (tokens.Length == 0)
            return null;

        var allTokens = new BooleanQuery.Builder();
        foreach (string token in tokens)
        {
            var acrossFields = new BooleanQuery.Builder().SetMinimumNumberShouldMatch(1);
            foreach (string field in fields)
                acrossFields.Add(new TermQuery(field, token), Occur.Should);
            allTokens.Add(acrossFields.Build(), Occur.Must);
        }

        return allTokens.Build();
    }

    private static string[] ResolveFields(LeanCorpusIndexMetadata metadata, LeanCorpusSchemaManifest schema)
    {
        IEnumerable<LeanCorpusFieldSchema> candidates = schema.Fields.Values
            .Where(field => field.Indexed && field.Representation == "analysed-text" && !field.Name.StartsWith("__lc_", StringComparison.Ordinal));
        if (metadata.SearchFields.Length > 0)
        {
            foreach (string selectedField in metadata.SearchFields)
            {
                if (!schema.Fields.TryGetValue(selectedField, out var selectedSchema)
                    || !selectedSchema.Indexed || selectedSchema.Representation != "analysed-text")
                    throw new InvalidDataException($"Search field '{selectedField}' is missing or is not analysed text.");
            }

            var selected = new HashSet<string>(metadata.SearchFields, StringComparer.Ordinal);
            candidates = candidates.Where(field => selected.Contains(field.Name));
        }

        return candidates.Select(field => field.Name).Distinct(StringComparer.Ordinal).OrderBy(field => field, StringComparer.Ordinal).ToArray();
    }

    private sealed class MaterialisedTokenSink : ISpanTokenSink
    {
        public List<string> Tokens { get; } = [];

        public void Add(ReadOnlySpan<char> text, int startOffset, int endOffset, string type = Token.DefaultType,
            int positionIncrement = 1, byte[]? payload = null)
            => Tokens.Add(text.ToString());

        public void Add(ReadOnlySpan<char> text, int startOffset, int endOffset, string type,
            int positionIncrement, int positionLength, byte[]? payload)
            => Tokens.Add(text.ToString());
    }
}
