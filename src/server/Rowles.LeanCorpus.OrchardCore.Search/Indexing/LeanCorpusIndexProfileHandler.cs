using Microsoft.Extensions.DependencyInjection;
using OrchardCore.Indexing.Core.Handlers;
using OrchardCore.Indexing;
using OrchardCore.Indexing.Models;

namespace Rowles.LeanCorpus.OrchardCore.Search.Indexing;

/// <summary>Coordinates provider-specific reset operations for Orchard index profiles.</summary>
public sealed class LeanCorpusIndexProfileHandler(IServiceProvider services) : IndexProfileHandlerBase
{
    /// <inheritdoc />
    public override async Task ResetAsync(IndexProfileResetContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(context.IndexProfile.ProviderName, "LeanCorpus", StringComparison.OrdinalIgnoreCase))
            return;

        if (services.GetRequiredKeyedService<IDocumentIndexManager>("LeanCorpus") is not LeanCorpusDocumentIndexManager manager)
            throw new InvalidOperationException("The LeanCorpus document-index manager is not registered under its provider key.");

        if (!await manager.ResetAsync(context.IndexProfile).ConfigureAwait(false))
            throw new InvalidOperationException($"The LeanCorpus index profile '{context.IndexProfile.Name}' could not be reset.");
    }
}
