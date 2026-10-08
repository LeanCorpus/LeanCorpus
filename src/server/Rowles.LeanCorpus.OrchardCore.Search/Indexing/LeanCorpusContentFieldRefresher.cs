using OrchardCore.ContentManagement;
using OrchardCore.ContentManagement.Metadata;
using OrchardCore.ContentManagement.Metadata.Models;
using OrchardCore.ContentManagement.Records;
using OrchardCore.Indexing;
using OrchardCore.Indexing.Models;
using YesSql;
using System.Linq.Expressions;

namespace Rowles.LeanCorpus.OrchardCore.Search.Indexing;

internal sealed class LeanCorpusContentFieldRefresher(
    IStore store,
    IContentDefinitionManager definitions,
    IEnumerable<IContentPartIndexHandler> partHandlers,
    IEnumerable<IContentFieldIndexHandler> fieldHandlers)
{
    private readonly IContentPartIndexHandler[] _partHandlers = partHandlers.ToArray();
    private readonly IContentFieldIndexHandler[] _fieldHandlers = fieldHandlers.ToArray();

    public async Task<DocumentIndex[]> RefreshAsync(DocumentIndex[] documents, IContentIndexSettings settings)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(settings);

        ContentItemDocumentIndex[] contentDocuments = documents.OfType<ContentItemDocumentIndex>()
            .Where(document => !string.IsNullOrWhiteSpace(document.ContentItemId)
                && !string.IsNullOrWhiteSpace(document.ContentItemVersionId))
            .ToArray();
        if (contentDocuments.Length == 0 || (_partHandlers.Length == 0 && _fieldHandlers.Length == 0))
            return documents;

        string[] versionIds = contentDocuments.Select(document => document.ContentItemVersionId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var items = new List<ContentItem>(contentDocuments.Length);
        using (ISession session = store.CreateSession())
        {
            foreach (string[] versionIdBatch in versionIds.Chunk(64))
            {
                Expression<Func<ContentItemIndex, bool>> predicate = CreateVersionPredicate(versionIdBatch);
                items.AddRange(await session.Query<ContentItem, ContentItemIndex>(predicate)
                    .ListAsync()
                    .ConfigureAwait(false));
            }
        }

        var itemsByVersion = items.ToDictionary(
            item => (item.ContentItemId, item.ContentItemVersionId),
            item => item);
        var refreshedDocuments = (DocumentIndex[])documents.Clone();
        for (int documentIndex = 0; documentIndex < documents.Length; documentIndex++)
        {
            if (documents[documentIndex] is not ContentItemDocumentIndex source
                || string.IsNullOrWhiteSpace(source.ContentItemId)
                || string.IsNullOrWhiteSpace(source.ContentItemVersionId))
            {
                continue;
            }

            if (!itemsByVersion.TryGetValue((source.ContentItemId, source.ContentItemVersionId), out ContentItem? item))
                continue;

            ContentTypeDefinition? typeDefinition = await definitions.LoadTypeDefinitionAsync(item.ContentType).ConfigureAwait(false);
            if (typeDefinition is null)
                continue;

            var refreshed = new ContentItemDocumentIndex(source.Id, source.ContentItemVersionId);
            var context = new BuildDocumentIndexContext(refreshed, item, [item.ContentType], settings);
            foreach (ContentTypePartDefinition typePart in typeDefinition.Parts)
            {
                ContentPart? contentPart = ((ContentElement)item).Get<ContentPart>(typePart.Name);
                if (contentPart is null)
                    continue;

                foreach (IContentPartIndexHandler handler in _partHandlers)
                    await handler.BuildIndexAsync(contentPart, typePart, context, settings).ConfigureAwait(false);

                foreach (ContentPartFieldDefinition field in typePart.PartDefinition.Fields)
                foreach (IContentFieldIndexHandler handler in _fieldHandlers)
                    await handler.BuildIndexAsync(contentPart, typePart, field, context, settings).ConfigureAwait(false);
            }

            if (refreshed.Entries.Count == 0)
                continue;

            HashSet<string> refreshedFieldNames = refreshed.Entries.Select(entry => entry.Name)
                .ToHashSet(StringComparer.Ordinal);
            var merged = new ContentItemDocumentIndex(source.Id, source.ContentItemVersionId);
            foreach (DocumentIndex.DocumentIndexEntry entry in source.Entries)
            {
                if (!refreshedFieldNames.Contains(entry.Name))
                    merged.Entries.Add(entry);
            }

            merged.Entries.AddRange(refreshed.Entries);
            refreshedDocuments[documentIndex] = merged;
        }

        return refreshedDocuments;
    }

    private static Expression<Func<ContentItemIndex, bool>> CreateVersionPredicate(string[] versionIds)
    {
        ParameterExpression index = Expression.Parameter(typeof(ContentItemIndex), "index");
        MemberExpression contentItemVersionId = Expression.Property(index, nameof(ContentItemIndex.ContentItemVersionId));
        Expression body = Expression.Equal(contentItemVersionId, Expression.Constant(versionIds[0]));
        for (int versionIndex = 1; versionIndex < versionIds.Length; versionIndex++)
        {
            body = Expression.OrElse(body,
                Expression.Equal(contentItemVersionId, Expression.Constant(versionIds[versionIndex])));
        }

        return Expression.Lambda<Func<ContentItemIndex, bool>>(body, index);
    }
}
