using OrchardCore.ContentManagement;
using OrchardCore.Indexing;

namespace Rowles.LeanCorpus.OrchardCore.Search.TestModule.Indexing;

/// <summary>Adds a deterministic Orchard vector entry for the end-to-end provider probe.</summary>
public sealed class VectorProbeIndexHandler : ContentPartIndexHandler<ContentPart>
{
    public const string ContentType = "VectorProbeArticle";
    public const string FieldName = "VectorProbe";
    public const int Dimensions = 3;

    public override Task BuildIndexAsync(ContentPart part, BuildPartIndexContext context)
    {
        if (context.Record is ContentItem item
            && item.ContentType.Equals(ContentType, StringComparison.Ordinal)
            && context.ContentTypePartDefinition.Name.Equals("TitlePart", StringComparison.Ordinal))
        {
            context.DocumentIndex.Entries.Add(new DocumentIndex.DocumentIndexEntry(
                FieldName,
                new[] { 1f, 0f, 0f },
                DocumentIndex.Types.Vector,
                DocumentIndexOptions.None)
            {
                Dimensions = Dimensions,
            });
        }

        return Task.CompletedTask;
    }
}
