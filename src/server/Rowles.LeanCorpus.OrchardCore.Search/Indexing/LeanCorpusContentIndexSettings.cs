using OrchardCore.Indexing;

namespace Rowles.LeanCorpus.OrchardCore.Search.Indexing;

internal sealed class LeanCorpusContentIndexSettings : IContentIndexSettings
{
    public bool Included { get; set; } = true;

    public DocumentIndexOptions ToOptions() => DocumentIndexOptions.None;
}
