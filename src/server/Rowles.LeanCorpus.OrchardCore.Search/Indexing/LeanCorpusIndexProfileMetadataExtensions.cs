using OrchardCore.Entities;
using OrchardCore.Indexing.Models;
using Rowles.LeanCorpus.OrchardCore.Search.Models;

namespace Rowles.LeanCorpus.OrchardCore.Search.Indexing;

internal static class LeanCorpusIndexProfileMetadataExtensions
{
    public const string EntityName = "LeanCorpus";

    public static LeanCorpusIndexMetadata GetLeanCorpusMetadata(
        this IndexProfile profile,
        LeanCorpusIndexMetadata? fallback = null)
        => profile.TryGet(EntityName, out LeanCorpusIndexMetadata metadata)
            ? metadata
            : fallback ?? new LeanCorpusIndexMetadata();

    public static bool HasLeanCorpusMetadata(this IndexProfile profile)
        => profile.Properties.ContainsKey(EntityName);

    public static bool MetadataEquals(LeanCorpusIndexMetadata left, LeanCorpusIndexMetadata right)
        => string.Equals(left.DefaultAnalyser, right.DefaultAnalyser, StringComparison.Ordinal)
            && (left.SearchFields ?? []).SequenceEqual(right.SearchFields ?? [], StringComparer.Ordinal);
}
