namespace Rowles.LeanCorpus.OrchardCore.Search.Indexing;

using Rowles.LeanCorpus.OrchardCore.Search.Models;

internal sealed record LeanCorpusSchemaManifest
{
    public int Version { get; init; } = 1;
    public Dictionary<string, LeanCorpusFieldSchema> Fields { get; init; } = new(StringComparer.Ordinal);
    public LeanCorpusIndexMetadata Metadata { get; init; } = new();
}
