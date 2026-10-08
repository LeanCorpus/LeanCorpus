using System.Globalization;
using System.Text.Json;
using Rowles.LeanCorpus.OrchardCore.Search.Indexing;
using Rowles.LeanCorpus.OrchardCore.Search.Models;
using Rowles.LeanCorpus.OrchardCore.Search.Search;
using Rowles.LeanCorpus.Search;
using Rowles.LeanCorpus.Search.Geo;
using Rowles.LeanCorpus.Search.Queries;

namespace Rowles.LeanCorpus.OrchardCore.Search.Queries;

internal sealed class LeanCorpusQueryCompiler
{
    public const int MaximumJsonLength = 16_384;
    public const int MaximumDepth = 16;
    public const int MaximumBooleanChildren = 32;
    public const int MaximumTake = 100;

    public (LeanCorpusQuery Request, Query Compiled) Compile(string json, LeanCorpusSchemaManifest schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(schema);
        if (json.Length > MaximumJsonLength)
            throw new InvalidDataException($"LeanCorpus query JSON may not exceed {MaximumJsonLength} characters.");

        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaximumDepth });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A LeanCorpus query must be a JSON object.");

        string index = RequiredString(root, "index");
        if (index.Length > 256)
            throw new InvalidDataException("The LeanCorpus index name is too long.");
        if (!root.TryGetProperty("query", out JsonElement queryElement))
            throw new InvalidDataException("A LeanCorpus query requires a 'query' object.");

        int skip = OptionalInt(root, "skip", 0);
        int take = OptionalInt(root, "take", 20);
        if (skip < 0 || skip > 1_000_000)
            throw new InvalidDataException("LeanCorpus query skip must be between 0 and 1000000.");
        if (take < 0 || take > MaximumTake)
            throw new InvalidDataException($"LeanCorpus query take must be between 0 and {MaximumTake}.");

        var request = new LeanCorpusQuery(index, queryElement.Clone(), skip, take);
        return (request, CompileClause(queryElement, schema, 0));
    }

    private static Query CompileClause(JsonElement node, LeanCorpusSchemaManifest schema, int depth)
    {
        if (depth >= MaximumDepth || node.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("LeanCorpus query clauses must be bounded JSON objects.");

        string operation = RequiredString(node, "type");
        return operation switch
        {
            "match" => CompileMatch(node, schema),
            "term" => CompileTerm(node, schema),
            "range" => CompileRange(node, schema),
            "exists" => CompileExists(node, schema),
            "isNull" or "is_null" => CompileIsNull(node, schema),
            "and" => CompileBoolean(node, schema, depth, Occur.Must),
            "or" => CompileBoolean(node, schema, depth, Occur.Should),
            "not" => CompileNot(node, schema, depth),
            "geoDistance" => CompileGeoDistance(node, schema),
            "vectorKnn" => CompileVector(node, schema),
            _ => throw new InvalidDataException($"Unknown LeanCorpus query operator '{operation}'."),
        };
    }

    private static Query CompileMatch(JsonElement node, LeanCorpusSchemaManifest schema)
    {
        string field = RequiredString(node, "field");
        LeanCorpusFieldSchema fieldSchema = RequireField(schema, field, "analysed-text");
        string value = RequiredString(node, "value");
        var metadata = new LeanCorpusIndexMetadata { DefaultAnalyser = fieldSchema.Analyser, SearchFields = [field] };
        return new LeanCorpusSearchCompiler().Compile(value, metadata, schema)
            ?? throw new InvalidDataException("The LeanCorpus match value contains no searchable tokens.");
    }

    private static Query CompileTerm(JsonElement node, LeanCorpusSchemaManifest schema)
    {
        string field = RequiredString(node, "field");
        LeanCorpusFieldSchema fieldSchema = RequireField(schema, field);
        string value = RequiredScalar(node, "value");
        switch (fieldSchema.Representation)
        {
            case "keyword":
            case "boolean-keyword":
                return new TermQuery(field, value);
            case "analysed-text":
                var metadata = new LeanCorpusIndexMetadata { DefaultAnalyser = fieldSchema.Analyser, SearchFields = [field] };
                Query? compiled = new LeanCorpusSearchCompiler().Compile(value, metadata, schema);
                if (compiled is BooleanQuery { Clauses.Count: 1 } group && group.Clauses[0].Query is BooleanQuery { Clauses.Count: 1 } oneField)
                    return oneField.Clauses[0].Query;
                throw new InvalidDataException("A LeanCorpus term query over analysed text must produce exactly one token.");
            case "int64":
                long integer = long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                return new Int64RangeQuery(field, integer, integer);
            case "double":
                double number = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
                return new RangeQuery(field, number, number);
            case "utc-ticks":
                long ticks = ParseUtcTicks(value);
                return new Int64RangeQuery(field, ticks, ticks);
            default:
                throw new InvalidDataException($"Term queries are not supported for LeanCorpus field '{field}'.");
        }
    }

    private static Query CompileRange(JsonElement node, LeanCorpusSchemaManifest schema)
    {
        string field = RequiredString(node, "field");
        LeanCorpusFieldSchema fieldSchema = RequireField(schema, field);
        string? lower = OptionalScalar(node, "gte") ?? OptionalScalar(node, "gt");
        string? upper = OptionalScalar(node, "lte") ?? OptionalScalar(node, "lt");
        bool includeLower = node.TryGetProperty("gte", out _);
        bool includeUpper = node.TryGetProperty("lte", out _);
        if (lower is null && upper is null)
            throw new InvalidDataException("A LeanCorpus range query requires at least one bound.");

        return fieldSchema.Representation switch
        {
            "int64" => new Int64RangeQuery(field,
                lower is null ? long.MinValue : long.Parse(lower, NumberStyles.Integer, CultureInfo.InvariantCulture),
                upper is null ? long.MaxValue : long.Parse(upper, NumberStyles.Integer, CultureInfo.InvariantCulture), includeLower, includeUpper),
            "utc-ticks" => new Int64RangeQuery(field,
                lower is null ? long.MinValue : ParseUtcTicks(lower),
                upper is null ? long.MaxValue : ParseUtcTicks(upper), includeLower, includeUpper),
            "double" => new RangeQuery(field,
                lower is null ? -double.MaxValue : double.Parse(lower, NumberStyles.Float, CultureInfo.InvariantCulture),
                upper is null ? double.MaxValue : double.Parse(upper, NumberStyles.Float, CultureInfo.InvariantCulture), includeLower, includeUpper),
            "keyword" or "boolean-keyword" => new TermRangeQuery(field, lower, upper, includeLower, includeUpper),
            _ => throw new InvalidDataException($"Range queries are not supported for LeanCorpus field '{field}'."),
        };
    }

    private static Query CompileExists(JsonElement node, LeanCorpusSchemaManifest schema)
    {
        string field = RequiredString(node, "field");
        _ = RequireField(schema, field);
        return new BooleanQuery.Builder()
            .Add(new FieldExistsQuery(field), Occur.Should)
            .Add(new TermQuery(field + LeanCorpusDocumentMapper.NullSuffix, "true"), Occur.Should)
            .SetMinimumNumberShouldMatch(1)
            .Build();
    }

    private static Query CompileIsNull(JsonElement node, LeanCorpusSchemaManifest schema)
    {
        string field = RequiredString(node, "field");
        _ = RequireField(schema, field);
        return new TermQuery(field + LeanCorpusDocumentMapper.NullSuffix, "true");
    }

    private static Query CompileBoolean(JsonElement node, LeanCorpusSchemaManifest schema, int depth, Occur occur)
    {
        if (!node.TryGetProperty("queries", out JsonElement children) || children.ValueKind != JsonValueKind.Array
            || children.GetArrayLength() is < 1 or > MaximumBooleanChildren)
            throw new InvalidDataException($"A LeanCorpus boolean query must contain 1 to {MaximumBooleanChildren} child clauses.");

        var builder = new BooleanQuery.Builder();
        if (occur == Occur.Should)
            builder.SetMinimumNumberShouldMatch(1);
        foreach (JsonElement child in children.EnumerateArray())
            builder.Add(CompileClause(child, schema, depth + 1), occur);
        return builder.Build();
    }

    private static Query CompileNot(JsonElement node, LeanCorpusSchemaManifest schema, int depth)
    {
        if (!node.TryGetProperty("query", out JsonElement child))
            throw new InvalidDataException("A LeanCorpus not query requires one 'query' clause.");
        return new BooleanQuery.Builder()
            .Add(new MatchAllDocsQuery(), Occur.Must)
            .Add(CompileClause(child, schema, depth + 1), Occur.MustNot)
            .Build();
    }

    private static Query CompileVector(JsonElement node, LeanCorpusSchemaManifest schema)
    {
        string field = RequiredString(node, "field");
        LeanCorpusFieldSchema fieldSchema = RequireField(schema, field, "vector");
        if (!node.TryGetProperty("value", out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("A LeanCorpus vectorKnn query requires a numeric vector value.");
        float[] vector = value.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetSingle(out float component) || !float.IsFinite(component))
                throw new InvalidDataException("LeanCorpus query vectors must contain finite numbers.");
            return component;
        }).ToArray();
        if (fieldSchema.Dimensions is not int dimensions || vector.Length != dimensions)
            throw new InvalidDataException("The LeanCorpus query vector dimensions do not match the indexed field.");
        int topK = OptionalInt(node, "topK", 10);
        if (topK is < 1 or > MaximumTake)
            throw new InvalidDataException($"LeanCorpus vectorKnn topK must be between 1 and {MaximumTake}.");
        return new VectorQuery(field, vector, topK);
    }

    private static Query CompileGeoDistance(JsonElement node, LeanCorpusSchemaManifest schema)
    {
        string field = RequiredString(node, "field");
        _ = RequireField(schema, field, "geo-point");
        double latitude = RequiredFiniteNumber(node, "latitude");
        double longitude = RequiredFiniteNumber(node, "longitude");
        double radiusMetres = RequiredFiniteNumber(node, "radiusMetres");
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180 || radiusMetres <= 0)
            throw new InvalidDataException("LeanCorpus geoDistance requires valid coordinates and a positive radius.");
        return new GeoDistanceQuery(field, latitude, longitude, radiusMetres);
    }

    private static LeanCorpusFieldSchema RequireField(LeanCorpusSchemaManifest schema, string field, string? representation = null)
    {
        if (string.IsNullOrWhiteSpace(field) || field.StartsWith("__lc_", StringComparison.Ordinal))
            throw new InvalidDataException("LeanCorpus queries cannot target internal or empty field names.");
        if (!schema.Fields.TryGetValue(field, out var fieldSchema))
            throw new InvalidDataException($"LeanCorpus field '{field}' is not present in the index schema.");
        if (!fieldSchema.Indexed || fieldSchema.Representation == "unsupported")
            throw new InvalidDataException($"LeanCorpus field '{field}' is not indexed.");
        if (representation is not null && fieldSchema.Representation != representation)
            throw new InvalidDataException($"LeanCorpus field '{field}' does not support this query operator.");
        return fieldSchema;
    }

    private static string RequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"LeanCorpus query property '{property}' must be a non-empty string.");
        return value.GetString()!;
    }

    private static string RequiredScalar(JsonElement element, string property)
        => OptionalScalar(element, property) ?? throw new InvalidDataException($"LeanCorpus query property '{property}' is required.");

    private static string? OptionalScalar(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => throw new InvalidDataException($"LeanCorpus query property '{property}' must be a string, number, or boolean."),
        };
    }

    private static int OptionalInt(JsonElement element, string property, int fallback)
    {
        if (!element.TryGetProperty(property, out JsonElement value))
            return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int parsed))
            throw new InvalidDataException($"LeanCorpus query property '{property}' must be an integer.");
        return parsed;
    }

    private static double RequiredFiniteNumber(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out double parsed)
            || !double.IsFinite(parsed))
            throw new InvalidDataException($"LeanCorpus query property '{property}' must be a finite number.");
        return parsed;
    }

    private static long ParseUtcTicks(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).UtcDateTime.Ticks;
}
