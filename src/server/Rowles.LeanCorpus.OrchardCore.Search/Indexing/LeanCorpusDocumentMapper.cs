using System.Collections;
using System.Globalization;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Microsoft.Extensions.Logging;
using OrchardCore.Indexing;
using Rowles.LeanCorpus.Document;
using Rowles.LeanCorpus.Document.Fields;

namespace Rowles.LeanCorpus.OrchardCore.Search.Indexing;

internal sealed class LeanCorpusDocumentMapper(ILogger<LeanCorpusDocumentMapper> logger)
{
    public const string DocumentIdField = "__lc_orchard_document_id";
    public const string ContentItemIdField = "__lc_orchard_content_item_id";
    public const string ContentItemVersionIdField = "__lc_orchard_content_item_version_id";
    public const string NullSuffix = ".__lc_null";

    public LeanCorpusMappedBatch Map(IEnumerable<DocumentIndex> documents, LeanCorpusSchemaManifest current)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(current);

        var nextFields = new Dictionary<string, LeanCorpusFieldSchema>(current.Fields, StringComparer.Ordinal);
        var mapped = new List<LeanDocument>();
        foreach (DocumentIndex documentIndex in documents)
        {
            ArgumentNullException.ThrowIfNull(documentIndex);
            if (string.IsNullOrWhiteSpace(documentIndex.Id))
                throw new InvalidDataException("Orchard supplied a document without an ID.");

            var document = new LeanDocument();
            document.Add(new StringField(DocumentIdField, documentIndex.Id));
            string contentItemId;
            if (documentIndex is ContentItemDocumentIndex contentItemDocument)
            {
                contentItemId = contentItemDocument.ContentItemId;
                string? versionId = contentItemDocument.ContentItemVersionId;
                document.Add(string.IsNullOrWhiteSpace(versionId)
                    ? new StringField(ContentItemVersionIdField + NullSuffix, "true", stored: false, boost: 1,
                        storeDocValues: false, indexOptions: FieldIndexOptions.DocsOnly)
                    : new StringField(ContentItemVersionIdField, versionId));
            }
            else
            {
                contentItemId = documentIndex.Id;
                document.Add(new StringField(ContentItemVersionIdField + NullSuffix, "true", stored: false, boost: 1,
                    storeDocValues: false, indexOptions: FieldIndexOptions.DocsOnly));
            }
            document.Add(new StringField(ContentItemIdField,
                string.IsNullOrWhiteSpace(contentItemId) ? documentIndex.Id : contentItemId));

            foreach (var group in documentIndex.Entries.GroupBy(entry => entry.Name, StringComparer.Ordinal))
            {
                ValidateFieldName(group.Key);
                var entries = group.ToArray();
                var first = entries[0];
                bool keyword = first.Type == DocumentIndex.Types.Text && first.Options.HasFlag(DocumentIndexOptions.Keyword);
                bool stored = first.Options.HasFlag(DocumentIndexOptions.Store);
                if (entries.Any(entry => entry.Type != first.Type
                    || (first.Type == DocumentIndex.Types.Text && entry.Options.HasFlag(DocumentIndexOptions.Keyword) != keyword)
                    || entry.Options.HasFlag(DocumentIndexOptions.Store) != stored))
                {
                    throw new InvalidDataException($"Orchard field '{group.Key}' has conflicting type or options within one document.");
                }

                var values = entries.SelectMany(ExpandValues).ToArray();
                int? dimensions = first.Type == DocumentIndex.Types.Vector
                    ? GetVectorDimensions(values, first.Dimensions, group.Key, nextFields.GetValueOrDefault(group.Key)?.Dimensions)
                    : null;
                var candidate = CreateSchema(group.Key, first.Type, keyword, stored, values.Length > 1, dimensions,
                    current.Metadata.DefaultAnalyser);
                if (nextFields.TryGetValue(group.Key, out var established))
                {
                    if (!Compatible(established, candidate))
                        throw new InvalidDataException($"Orchard field '{group.Key}' conflicts with its persisted LeanCorpus schema.");
                    if (established.Dimensions is null && candidate.Dimensions is not null)
                        nextFields[group.Key] = established with { Dimensions = candidate.Dimensions };
                    if (!established.MultiValued && candidate.MultiValued)
                        nextFields[group.Key] = nextFields[group.Key] with { MultiValued = true };
                }
                else
                {
                    nextFields.Add(group.Key, candidate);
                }

                if (first.Type == DocumentIndex.Types.Complex)
                {
                    logger.LogDebug("LeanCorpus does not index Orchard Complex field {FieldName}.", group.Key);
                    continue;
                }

                foreach (object? value in values)
                {
                    if (value is null)
                    {
                        document.Add(new StringField(group.Key + NullSuffix, "true", stored: false, boost: 1, storeDocValues: false,
                            indexOptions: FieldIndexOptions.DocsOnly));
                        continue;
                    }

                    document.Add(MapValue(group.Key, value, first, keyword, stored));
                }
            }

            mapped.Add(document);
        }

        return new LeanCorpusMappedBatch(mapped, new LeanCorpusSchemaManifest
        {
            Fields = nextFields,
            Metadata = current.Metadata,
        });
    }

    private static void ValidateFieldName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith("__lc_", StringComparison.Ordinal)
            || name.EndsWith(NullSuffix, StringComparison.Ordinal)
            || name.Equals(DocumentIdField, StringComparison.Ordinal)
            || name.Equals(ContentItemIdField, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Orchard field name '{name}' is empty or reserved by LeanCorpus.");
        }
    }

    private static LeanCorpusFieldSchema CreateSchema(
        string name,
        DocumentIndex.Types type,
        bool keyword,
        bool stored,
        bool multiValued,
        int? dimensions,
        string defaultAnalyser)
    {
        string representation = type switch
        {
            DocumentIndex.Types.Text when keyword => "keyword",
            DocumentIndex.Types.Text => "analysed-text",
            DocumentIndex.Types.Boolean => "boolean-keyword",
            DocumentIndex.Types.Integer => "int64",
            DocumentIndex.Types.Number => "double",
            DocumentIndex.Types.DateTime => "utc-ticks",
            DocumentIndex.Types.GeoPoint => "geo-point",
            DocumentIndex.Types.Vector => "vector",
            DocumentIndex.Types.Complex => "unsupported",
            _ => throw new InvalidDataException($"Orchard field '{name}' has unknown type '{type}'."),
        };
        bool supported = type != DocumentIndex.Types.Complex;
        return new LeanCorpusFieldSchema
        {
            Name = name,
            OrchardType = type.ToString(),
            Representation = representation,
            Indexed = supported,
            Stored = stored || type is DocumentIndex.Types.GeoPoint or DocumentIndex.Types.Vector,
            Keyword = keyword,
            MultiValued = multiValued,
            Analyser = type == DocumentIndex.Types.Text && !keyword ? defaultAnalyser : "none",
            NullPolicy = "companion-marker",
            Dimensions = dimensions,
        };
    }

    private static bool Compatible(LeanCorpusFieldSchema established, LeanCorpusFieldSchema candidate)
        => established.Name == candidate.Name
            && established.OrchardType == candidate.OrchardType
            && established.Representation == candidate.Representation
            && established.Indexed == candidate.Indexed
            && established.Stored == candidate.Stored
            && established.Keyword == candidate.Keyword
            && established.Analyser == candidate.Analyser
            && established.NullPolicy == candidate.NullPolicy
            && (established.Dimensions == candidate.Dimensions
                || established.OrchardType == nameof(DocumentIndex.Types.Vector)
                    && (established.Dimensions is null || candidate.Dimensions is null));

    private static IEnumerable<object?> ExpandValues(DocumentIndex.DocumentIndexEntry entry)
    {
        object? value = entry.Value;
        if (entry.Type == DocumentIndex.Types.Vector || value is null or string or IHtmlContent || value is not IEnumerable enumerable)
        {
            yield return value;
            yield break;
        }

        foreach (object? item in enumerable)
            yield return item;
    }

    private static IField MapValue(string name, object value, DocumentIndex.DocumentIndexEntry entry, bool keyword, bool stored)
        => entry.Type switch
        {
            DocumentIndex.Types.Text => MapText(name, value, keyword, stored),
            DocumentIndex.Types.Boolean => new StringField(name, Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? "true" : "false", stored),
            DocumentIndex.Types.Integer => new Int64Field(name, Convert.ToInt64(value, CultureInfo.InvariantCulture), stored),
            DocumentIndex.Types.Number => MapNumber(name, value, stored),
            DocumentIndex.Types.DateTime => new Int64Field(name, ToUtcTicks(value), stored),
            DocumentIndex.Types.GeoPoint => MapGeoPoint(name, value),
            DocumentIndex.Types.Vector => MapVector(name, value, entry.Dimensions),
            DocumentIndex.Types.Complex => throw new InvalidOperationException("Complex values are not mapped."),
            _ => throw new InvalidDataException($"Orchard field '{name}' has an unsupported type '{entry.Type}'."),
        };

    private static IField MapText(string name, object value, bool keyword, bool stored)
    {
        string text = value switch
        {
            string stringValue => stringValue,
            IHtmlContent html => RenderHtml(html),
            _ => throw new InvalidDataException($"Orchard text field '{name}' contains {value.GetType().Name} instead of text."),
        };
        return keyword ? new StringField(name, text, stored) : new TextField(name, text, stored);
    }

    private static string RenderHtml(IHtmlContent content)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        content.WriteTo(writer, HtmlEncoder.Default);
        return writer.ToString();
    }

    private static NumericField MapNumber(string name, object value, bool stored)
    {
        double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (!double.IsFinite(number))
            throw new InvalidDataException($"Orchard number field '{name}' must be finite.");
        return new NumericField(name, number, stored);
    }

    private static long ToUtcTicks(object value)
    {
        DateTime utc = value switch
        {
            DateTimeOffset offset => offset.UtcDateTime,
            DateTime dateTime when dateTime.Kind == DateTimeKind.Unspecified => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
            DateTime dateTime => dateTime.ToUniversalTime(),
            _ => throw new InvalidDataException($"Orchard DateTime value '{value.GetType().Name}' is unsupported."),
        };
        return utc.Ticks;
    }

    private static GeoPointField MapGeoPoint(string name, object value)
    {
        if (value is not DocumentIndex.GeoPoint point)
            throw new InvalidDataException($"Orchard GeoPoint field '{name}' contains an unsupported value.");
        if (point.Latitude is < -90m or > 90m || point.Longitude is < -180m or > 180m)
            throw new InvalidDataException($"Orchard GeoPoint field '{name}' has latitude or longitude outside its valid range.");
        return new GeoPointField(name, (double)point.Latitude, (double)point.Longitude);
    }

    private static VectorField MapVector(string name, object value, int dimensions)
    {
        if (value is not float[] vector || dimensions <= 0 || dimensions != vector.Length)
            throw new InvalidDataException($"Orchard Vector field '{name}' has inconsistent dimensions.");
        if (vector.Any(component => !float.IsFinite(component)))
            throw new InvalidDataException($"Orchard Vector field '{name}' must contain finite values.");
        return new VectorField(name, vector.AsMemory());
    }

    private static int? GetVectorDimensions(object?[] values, int dimensions, string name, int? establishedDimensions)
    {
        if (values.Length == 1 && values[0] is null)
            return dimensions > 0 ? dimensions : establishedDimensions;
        if (values.Length != 1 || values[0] is not float[] vector || dimensions <= 0 || dimensions != vector.Length)
            throw new InvalidDataException($"Orchard Vector field '{name}' has inconsistent dimensions.");
        return vector.Length;
    }
}

internal sealed record LeanCorpusMappedBatch(
    IReadOnlyList<LeanDocument> Documents,
    LeanCorpusSchemaManifest Manifest);
