namespace Rowles.LeanCorpus.OrchardCore.Search.Indexing;

internal sealed record LeanCorpusFieldSchema
{
    public required string Name { get; init; }
    public required string OrchardType { get; init; }
    public required string Representation { get; init; }
    public bool Indexed { get; init; }
    public bool Stored { get; init; }
    public bool Keyword { get; init; }
    public bool MultiValued { get; init; }
    public required string Analyser { get; init; }
    public required string NullPolicy { get; init; }
    public int? Dimensions { get; init; }
}
