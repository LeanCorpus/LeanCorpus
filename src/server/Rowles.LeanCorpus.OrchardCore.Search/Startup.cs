using OrchardCore.Modules;
using OrchardCore.Indexing.Core;
using OrchardCore.Queries;
using OrchardCore.Search.Abstractions;
using OrchardCore.Search;
using OrchardCore.DisplayManagement.Handlers;
using OrchardCore.Indexing.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Rowles.LeanCorpus.OrchardCore.Search.Indexing;
using Rowles.LeanCorpus.OrchardCore.Search.Models;
using Rowles.LeanCorpus.OrchardCore.Search.Queries;
using Rowles.LeanCorpus.OrchardCore.Search.Search;
using Rowles.LeanCorpus.OrchardCore.Search.Storage;
using Rowles.LeanCorpus.OrchardCore.Search.Drivers;

namespace Rowles.LeanCorpus.OrchardCore.Search;

/// <summary>Registers the LeanCorpus Orchard Core provider.</summary>
public sealed class Startup : StartupBase
{
    public override void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<LeanCorpusIndexPathResolver>();
        services.AddSingleton<LeanCorpusIndexHandleCache>();
        services.AddSingleton<LeanCorpusSchemaStore>();
        services.AddSingleton<LeanCorpusDocumentMapper>();
        services.AddScoped<LeanCorpusIndexNameProvider>();
        services.AddScoped<LeanCorpusContentFieldRefresher>();
        services.AddSingleton<LeanCorpusSearchCompiler>();
        services.AddSingleton<LeanCorpusQueryCompiler>();
        services.AddSingleton<ILeanCorpusFailureInjector, NoOpLeanCorpusFailureInjector>();

        services.AddIndexingSource<LeanCorpusIndexManager, LeanCorpusDocumentIndexManager, LeanCorpusIndexNameProvider>(
            "LeanCorpus",
            "Content",
            entry =>
            {
                entry.DisplayName = new LocalizedString("LeanCorpus", "LeanCorpus");
                entry.Description = new LocalizedString("Embedded LeanCorpus index", "Embedded LeanCorpus index");
            },
            provider => provider.DisplayName = new LocalizedString("LeanCorpus", "LeanCorpus"));
        services.AddIndexProfileHandler<LeanCorpusIndexProfileHandler>();

        services.AddSearchService<LeanCorpusSearchService>("LeanCorpus");
        services.AddScoped<IQuerySource, LeanCorpusQuerySource>();
        services.AddScoped<IDisplayDriver<IndexProfile>, LeanCorpusIndexProfileDisplayDriver>();
        services.AddScoped<IDisplayDriver<Query>, LeanCorpusQueryDisplayDriver>();
    }
}
