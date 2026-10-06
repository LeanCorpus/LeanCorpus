using System.Globalization;
using System.Text.Json;
using Rowles.LeanCorpus.Index;
using Rowles.LeanCorpus.Index.Format;
using Rowles.LeanCorpus.Search.Searcher;
using Rowles.LeanCorpus.Store;

namespace Rowles.LeanCorpus.CompoundDurabilitySpike;

internal sealed record SegmentTopology(
    int SegmentOrdinal,
    string SegmentId,
    int MinDocumentOrdinal,
    int MaxDocumentOrdinal,
    int DocumentCount,
    int PhysicalFileCount,
    long PhysicalBytes);

internal sealed record IndexTopology(IReadOnlyList<SegmentTopology> Segments, int? CommitGeneration = null)
{
    public int SegmentCount => Segments.Count;

    public string DocumentVector => JsonSerializer.Serialize(Segments.Select(static segment => new
    {
        min_document_ordinal = segment.MinDocumentOrdinal,
        max_document_ordinal = segment.MaxDocumentOrdinal,
        document_count = segment.DocumentCount
    }));
}

internal static class Topology
{
    public static IndexTopology Read(string indexPath, string representation, bool inspectEveryDocument)
    {
        if (!Directory.Exists(indexPath))
            throw new DirectoryNotFoundException(indexPath);

        string[] commitFiles = Directory.GetFiles(indexPath, "segments_*")
            .Where(static path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(".pending", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (commitFiles.Length == 0)
        {
            if (Directory.EnumerateFiles(indexPath).Any())
                throw new InvalidDataException($"Fresh index '{indexPath}' has files but no committed topology.");
            return new IndexTopology([]);
        }

        using var directory = new MMapDirectory(indexPath);
        IndexFormatInventory inventory = IndexFormatInspector.Inspect(directory);
        if (inventory.CommitGeneration is null)
            throw new InvalidDataException($"Index '{indexPath}' has no readable commit.");
        if (inventory.Issues.Any(static issue => issue.Severity == IndexCheckSeverity.Error))
            throw new InvalidDataException($"Index '{indexPath}' has format inspection errors: {string.Join(" | ", inventory.Issues.Select(static issue => issue.Message))}");
        if (inventory.Segments.Count != inventory.SegmentIds.Count)
            throw new InvalidDataException($"Index '{indexPath}' exposes {inventory.Segments.Count} segment inventories for {inventory.SegmentIds.Count} committed segments.");

        if (inventory.SegmentIds.Count == 0)
            return new IndexTopology([]);

        using var searcher = new IndexSearcher(new MMapDirectory(indexPath));
        var storedIdOnly = new HashSet<string>(StringComparer.Ordinal) { "id" };
        var rows = new List<SegmentTopology>(inventory.Segments.Count);
        int documentBase = 0;
        for (int segmentIndex = 0; segmentIndex < inventory.Segments.Count; segmentIndex++)
        {
            SegmentFormatInventory segment = inventory.Segments[segmentIndex];
            if (segment.DocCount is not int documentCount || documentCount <= 0)
                throw new InvalidDataException($"Segment '{segment.SegmentId}' has no valid document count.");

            int minOrdinal = int.MaxValue;
            int maxOrdinal = int.MinValue;
            if (inspectEveryDocument)
            {
                int firstOrdinal = -1;
                for (int localDocument = 0; localDocument < documentCount; localDocument++)
                {
                    int ordinal = ReadDocumentOrdinal(searcher.GetStoredFields(documentBase + localDocument, storedIdOnly), segment.SegmentId);
                    if (localDocument == 0)
                        firstOrdinal = ordinal;
                    minOrdinal = Math.Min(minOrdinal, ordinal);
                    maxOrdinal = Math.Max(maxOrdinal, ordinal);
                    if (localDocument > 0 && ordinal != firstOrdinal + localDocument)
                        throw new InvalidDataException($"Segment '{segment.SegmentId}' has a non-contiguous document-ID sequence at local document {localDocument}.");
                }
            }
            else
            {
                minOrdinal = ReadDocumentOrdinal(searcher.GetStoredFields(documentBase, storedIdOnly), segment.SegmentId);
                maxOrdinal = ReadDocumentOrdinal(searcher.GetStoredFields(documentBase + documentCount - 1, storedIdOnly), segment.SegmentId);
            }

            if (maxOrdinal - minOrdinal + 1 != documentCount)
                throw new InvalidDataException($"Segment '{segment.SegmentId}' spans {minOrdinal}..{maxOrdinal} for {documentCount} documents.");

            string[] physicalFiles = Directory.EnumerateFiles(indexPath)
                .Where(path => IsPhysicalSegmentFile(Path.GetFileName(path), segment.SegmentId))
                .ToArray();
            int physicalFileCount = physicalFiles.Length;
            long physicalBytes = physicalFiles.Sum(static path => new FileInfo(path).Length);
            bool hasCompound = physicalFiles.Any(static path => path.EndsWith(".cfs", StringComparison.OrdinalIgnoreCase));
            if (representation == "compound" && !hasCompound)
                throw new InvalidDataException($"Segment '{segment.SegmentId}' has no physical .cfs file in compound mode.");
            if (representation == "loose" && hasCompound)
                throw new InvalidDataException($"Loose segment '{segment.SegmentId}' unexpectedly has a .cfs file.");

            rows.Add(new SegmentTopology(segmentIndex, segment.SegmentId, minOrdinal, maxOrdinal,
                documentCount, physicalFileCount, physicalBytes));
            documentBase = checked(documentBase + documentCount);
        }

        if (documentBase != searcher.Stats.TotalDocCount)
            throw new InvalidDataException($"Topology counts total {documentBase} documents, but the reopened index reports {searcher.Stats.TotalDocCount}.");
        return new IndexTopology(rows, inventory.CommitGeneration);
    }

    public static bool HasExpectedBaseline(IndexTopology topology)
    {
        if (topology.SegmentCount != 9)
            return false;
        for (int index = 0; index < topology.Segments.Count; index++)
        {
            SegmentTopology segment = topology.Segments[index];
            int expectedMin = index * 10_000;
            if (segment.SegmentOrdinal != index || segment.MinDocumentOrdinal != expectedMin
                || segment.MaxDocumentOrdinal != expectedMin + 9_999 || segment.DocumentCount != 10_000)
                return false;
        }
        return true;
    }

    public static void WriteManifest(string path, string representation, IndexTopology topology)
    {
        Csv.Create(path, [
            "segment_ordinal", "segment_id", "min_document_ordinal", "max_document_ordinal",
            "document_count", "representation", "physical_file_count", "physical_bytes"]);
        foreach (SegmentTopology segment in topology.Segments)
        {
            Csv.Append(path,
                ["segment_ordinal", "segment_id", "min_document_ordinal", "max_document_ordinal",
                    "document_count", "representation", "physical_file_count", "physical_bytes"],
                [segment.SegmentOrdinal, segment.SegmentId, segment.MinDocumentOrdinal, segment.MaxDocumentOrdinal,
                    segment.DocumentCount, representation, segment.PhysicalFileCount, segment.PhysicalBytes]);
        }
    }

    private static int ReadDocumentOrdinal(IReadOnlyDictionary<string, IReadOnlyList<string>> storedFields, string segmentId)
    {
        if (!storedFields.TryGetValue("id", out IReadOnlyList<string>? values) || values.Count != 1)
            throw new InvalidDataException($"A document in segment '{segmentId}' does not have exactly one stored id field.");
        string id = values[0];
        int separator = id.LastIndexOf('-');
        if (separator < 0 || !int.TryParse(id.AsSpan(separator + 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out int ordinal))
            throw new InvalidDataException($"Stored id '{id}' in segment '{segmentId}' has no numeric DataForge ordinal.");
        return ordinal;
    }

    private static bool IsPhysicalSegmentFile(string fileName, string segmentId)
        => fileName.StartsWith(segmentId + ".", StringComparison.Ordinal)
            || fileName.StartsWith(segmentId + "_gen_", StringComparison.Ordinal);
}
