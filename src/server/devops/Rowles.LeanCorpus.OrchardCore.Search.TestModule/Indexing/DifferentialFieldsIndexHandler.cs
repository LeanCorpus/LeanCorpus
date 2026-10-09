using System.Text.Json.Nodes;
using OrchardCore.ContentManagement;
using OrchardCore.Indexing;

namespace Rowles.LeanCorpus.OrchardCore.Search.TestModule.Indexing;

/// <summary>Adds deterministic keyword and stored fields shared by the paired provider probes.</summary>
public sealed class DifferentialFieldsIndexHandler : ContentPartIndexHandler<ContentPart>
{
    public const string MultiValueField = "DifferentialMultiValue";
    public const string StoredField = "DifferentialStored";

    public override Task BuildIndexAsync(ContentPart part, BuildPartIndexContext context)
    {
        if (context.Record is not ContentItem item
            || !item.ContentType.Equals("SearchTestArticle", StringComparison.Ordinal)
            || !context.ContentTypePartDefinition.Name.Equals("SearchTestArticle", StringComparison.Ordinal))
            return Task.CompletedTask;

        string? keywords = GetText(item.Content, "Keywords");
        if (!string.IsNullOrWhiteSpace(keywords))
        {
            foreach (string value in keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                context.DocumentIndex.Entries.Add(new DocumentIndex.DocumentIndexEntry(
                    MultiValueField,
                    value,
                    DocumentIndex.Types.Text,
                    DocumentIndexOptions.Keyword));
            }
        }

        context.DocumentIndex.Entries.Add(new DocumentIndex.DocumentIndexEntry(
            StoredField,
            $"stored-{item.ContentItemId}",
            DocumentIndex.Types.Text,
            DocumentIndexOptions.Keyword | DocumentIndexOptions.Store));

        return Task.CompletedTask;
    }

    private static string? GetText(JsonObject content, string fieldName)
        => content["SearchTestArticle"]?[fieldName]?["Text"]?.GetValue<string>();
}
