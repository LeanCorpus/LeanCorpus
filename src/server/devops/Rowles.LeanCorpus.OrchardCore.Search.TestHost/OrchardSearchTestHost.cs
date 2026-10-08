using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using OrchardCore.ContentManagement;
using OrchardCore.ContentManagement.Metadata;
using OrchardCore.ContentManagement.Metadata.Models;
using OrchardCore.ContentFields.Fields;
using OrchardCore.ContentFields.Indexing;
using OrchardCore.Indexing;
using OrchardCore.Indexing.Core;
using OrchardCore.Indexing.Models;
using OrchardCore.Search.Abstractions;
using Rowles.LeanCorpus.OrchardCore.Search.Indexing;
using System.Text.Json.Nodes;
using YesSqlSession = YesSql.ISession;

public static class OrchardSearchTestHost
{
    public static WebApplication Build(string[]? args = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args ?? [],
            ApplicationName = typeof(OrchardSearchTestHost).Assembly.GetName().Name,
        });
        Configure(builder);
        return BuildApplication(builder);
    }

    public static async Task<(WebApplication App, Uri Address)> StartIsolatedAsync(string contentRoot)
    {
        Directory.CreateDirectory(contentRoot);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(OrchardSearchTestHost).Assembly.GetName().Name,
            ContentRootPath = Path.GetFullPath(contentRoot),
            EnvironmentName = "Development",
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OrchardCore_AutoSetup:AutoSetupPath"] = string.Empty,
            ["OrchardCore_AutoSetup:Tenants:0:ShellName"] = "Default",
            ["OrchardCore_AutoSetup:Tenants:0:SiteName"] = "LeanCorpus Orchard Search Test Site",
            ["OrchardCore_AutoSetup:Tenants:0:SiteTimeZone"] = "UTC",
            ["OrchardCore_AutoSetup:Tenants:0:AdminUsername"] = "admin",
            ["OrchardCore_AutoSetup:Tenants:0:AdminEmail"] = "admin@example.test",
            ["OrchardCore_AutoSetup:Tenants:0:AdminPassword"] = "OrchardTestPassword!39",
            ["OrchardCore_AutoSetup:Tenants:0:DatabaseProvider"] = "Sqlite",
            ["OrchardCore_AutoSetup:Tenants:0:DatabaseConnectionString"] = string.Empty,
            ["OrchardCore_AutoSetup:Tenants:0:DatabaseTablePrefix"] = string.Empty,
            ["OrchardCore_AutoSetup:Tenants:0:RecipeName"] = "LeanCorpusSearchTest",
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        Configure(builder);
        WebApplication app = BuildApplication(builder);
        await app.StartAsync().ConfigureAwait(false);
        string address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses.Single()
            ?? throw new InvalidOperationException("The Orchard test host did not publish an address.");
        return (app, new Uri(address));
    }

    private static void Configure(WebApplicationBuilder builder)
    {
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddOrchardCms(orchard => orchard.AddSetupFeatures(
                "OrchardCore.AutoSetup",
                "Rowles.LeanCorpus.OrchardCore.Search",
                "Rowles.LeanCorpus.OrchardCore.Search.TestHostModule"));
    }

    private static WebApplication BuildApplication(WebApplicationBuilder builder)
    {
        var app = builder.Build();
        app.UseOrchardCore();

        app.MapPost("/__test/articles", async ([FromBody] TestArticleRequest request, [FromServices] IContentManager contentManager) =>
        {
            ContentItem item = await contentManager.NewAsync("SearchTestArticle").ConfigureAwait(false);
            SetArticle(item, request);
            if (!await contentManager.CreateAsync(item).ConfigureAwait(false))
                return Results.Problem("Orchard did not create the SearchTestArticle item.", statusCode: StatusCodes.Status422UnprocessableEntity);
            if (!await contentManager.PublishAsync(item).ConfigureAwait(false))
                return Results.Problem("Orchard did not publish the SearchTestArticle item.", statusCode: StatusCodes.Status422UnprocessableEntity);
            return Results.Ok(new TestArticleResponse(item.ContentItemId));
        });

        app.MapPost("/__test/articles/bulk", async (
            [FromBody] BulkArticleRequest request,
            [FromServices] IContentItemIdGenerator idGenerator,
            [FromServices] YesSqlSession session) =>
        {
            if (request.Count is < 1 or > 2000 || string.IsNullOrWhiteSpace(request.SharedTerm))
                return Results.BadRequest("Bulk content requires 1 to 2,000 items and a shared search term.");

            var contentItems = new ContentItem[request.Count];
            for (int index = 0; index < request.Count; index++)
            {
                var item = new ContentItem { ContentType = "SearchTestArticle" };
                item.ContentItemId = idGenerator.GenerateUniqueId(item);
                item.ContentItemVersionId = idGenerator.GenerateUniqueId(item);
                SetPerformanceArticle(item, new TestArticleRequest(
                    $"Orchard performance article {index:D4}",
                    $"{request.SharedTerm} orchard performance document {index:D4} deterministic body"));
                item.ContentItemVersionId = idGenerator.GenerateUniqueId(item);
                item.Published = true;
                item.Latest = true;
                contentItems[index] = item;
            }

            // This endpoint seeds only the explicit performance corpus. Persisting through YesSQL
            // avoids measuring 2,000 content-task handler executions as provider build time.
            foreach (ContentItem item in contentItems)
                await session.SaveAsync(item).ConfigureAwait(false);

            return Results.Ok(new BulkArticleResponse(contentItems
                .Select(item => new ContentItemIdentity(item.ContentItemId, item.ContentItemVersionId))
                .ToArray()));
        });

        app.MapPut("/__test/articles/{id}", async (string id, [FromBody] TestArticleRequest request, [FromServices] IContentManager contentManager) =>
        {
            ContentItem? item = await contentManager.GetAsync(id, VersionOptions.Latest).ConfigureAwait(false);
            if (item is null)
                return Results.NotFound();
            SetArticle(item, request);
            await contentManager.UpdateAsync(item).ConfigureAwait(false);
            if (!await contentManager.PublishAsync(item).ConfigureAwait(false))
                return Results.Problem("Orchard did not publish the updated SearchTestArticle item.", statusCode: StatusCodes.Status422UnprocessableEntity);
            return Results.Ok(new TestArticleResponse(item.ContentItemId));
        });

        app.MapDelete("/__test/articles/{id}", async (string id, [FromServices] IContentManager contentManager) =>
        {
            ContentItem? item = await contentManager.GetAsync(id, VersionOptions.Latest).ConfigureAwait(false);
            if (item is null)
                return Results.NotFound();
            return await contentManager.UnpublishAsync(item).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.Problem("Orchard did not unpublish the SearchTestArticle item.", statusCode: StatusCodes.Status422UnprocessableEntity);
        });

        app.MapGet("/__test/search/{provider}/{name}", async (
            string provider,
            string name,
            string terms,
            int start,
            int size,
            [FromServices] IIndexProfileManager profiles,
            [FromServices] IEnumerable<ISearchService> searchServices) =>
        {
            IndexProfile? profile = await profiles.FindByNameAndProviderAsync(name, provider).ConfigureAwait(false);
            ISearchService? searchService = searchServices.SingleOrDefault(service => service.Name == provider);
            if (profile is null || searchService is null)
                return Results.NotFound();

            var result = await searchService.SearchAsync(profile, terms, start, size).ConfigureAwait(false);
            return Results.Ok(new SearchSnapshot(result.Success, result.TotalCount, result.ContentItemIds.ToArray()));
        });

        app.MapGet("/__test/field-index-handlers", ([FromServices] IEnumerable<IContentFieldIndexHandler> handlers) =>
            Results.Ok(handlers.Select(handler => handler.GetType().Name).Order(StringComparer.Ordinal).ToArray()));

        app.MapPost("/__test/indexing/process", async (IServiceProvider services) =>
        {
            ContentIndexingService? indexing = services.GetService<ContentIndexingService>();
            if (indexing is null)
                return Results.Problem("Orchard did not register its content indexing service.", statusCode: StatusCodes.Status503ServiceUnavailable);
            long started = Stopwatch.GetTimestamp();
            await indexing.ProcessRecordsForAllIndexesAsync().ConfigureAwait(false);
            return Results.Ok(new PerformanceProcessingSnapshot(Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        });

        app.MapPost("/__test/performance/search/{provider}/{name}", async (
            string provider,
            string name,
            [FromBody] PerformanceSearchRequest request,
            [FromServices] IIndexProfileManager profiles,
            [FromServices] IEnumerable<ISearchService> searchServices) =>
        {
            IndexProfile? profile = await profiles.FindByNameAndProviderAsync(name, provider).ConfigureAwait(false);
            ISearchService? searchService = searchServices.SingleOrDefault(service => service.Name == provider);
            if (profile is null || searchService is null)
                return Results.NotFound();

            long started = Stopwatch.GetTimestamp();
            var result = await searchService.SearchAsync(profile, request.Term, request.Start, request.Size).ConfigureAwait(false);
            double elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return Results.Ok(new PerformanceSearchSnapshot(
                elapsedMilliseconds,
                result.Success,
                result.TotalCount,
                result.ContentItemIds.ToArray()));
        });

        app.MapPost("/__test/performance/build/{provider}/{name}", async (
            string provider,
            string name,
            [FromBody] PerformanceBuildRequest request,
            [FromServices] IIndexProfileManager profiles,
            [FromServices] IIndexingTaskManager tasks,
            [FromServices] IContentManager contentManager,
            [FromServices] IContentDefinitionManager definitions,
            [FromServices] IEnumerable<IContentPartIndexHandler> partHandlers,
            [FromServices] IEnumerable<IContentFieldIndexHandler> fieldHandlers,
            IServiceProvider services) =>
        {
            IndexProfile? profile = await profiles.FindByNameAndProviderAsync(name, provider).ConfigureAwait(false);
            if (profile is null || request.Items.Length == 0)
                return Results.NotFound();

            long started = Stopwatch.GetTimestamp();
            IIndexManager indexManager = services.GetRequiredKeyedService<IIndexManager>(provider);
            IDocumentIndexManager documentManager = services.GetRequiredKeyedService<IDocumentIndexManager>(provider);
            if (!await indexManager.RebuildAsync(profile).ConfigureAwait(false))
                return Results.Problem("The Orchard index provider could not rebuild the requested performance profile.", statusCode: StatusCodes.Status409Conflict);

            string[] contentItemIds = request.Items.Select(item => item.ContentItemId).ToArray();
            ContentItem[] contentItems = (await contentManager.GetAsync(contentItemIds, VersionOptions.Published).ConfigureAwait(false)).ToArray();
            if (contentItems.Length != request.Items.Length)
                return Results.Problem("Orchard did not load the full performance corpus.", statusCode: StatusCodes.Status409Conflict);

            IContentIndexSettings settings = documentManager.GetContentIndexSettings();
            var builtContentDocuments = new List<ContentItemDocumentIndex>(contentItems.Length);
            foreach (ContentItem contentItem in contentItems)
            {
                var contentItemDocument = new ContentItemDocumentIndex(contentItem.ContentItemId, contentItem.ContentItemVersionId);
                ContentTypeDefinition? typeDefinition = await definitions.LoadTypeDefinitionAsync(contentItem.ContentType).ConfigureAwait(false);
                if (typeDefinition is not null)
                {
                    var context = new BuildDocumentIndexContext(contentItemDocument, contentItem, [contentItem.ContentType], settings);
                    foreach (ContentTypePartDefinition typePart in typeDefinition.Parts)
                    {
                        ContentPart? contentPart = ((ContentElement)contentItem).Get<ContentPart>(typePart.Name);
                        if (contentPart is null)
                            continue;

                        foreach (IContentPartIndexHandler handler in partHandlers)
                            await handler.BuildIndexAsync(contentPart, typePart, context, settings).ConfigureAwait(false);

                        foreach (ContentPartFieldDefinition field in typePart.PartDefinition.Fields)
                        foreach (IContentFieldIndexHandler handler in fieldHandlers)
                            await handler.BuildIndexAsync(contentPart, typePart, field, context, settings).ConfigureAwait(false);
                    }
                }

                builtContentDocuments.Add(contentItemDocument);
            }

            DocumentIndex[] documents = provider.Equals("LeanCorpus", StringComparison.Ordinal)
                ? builtContentDocuments.Select(contentItemDocument =>
                {
                    var document = new DocumentIndex(contentItemDocument.Id);
                    document.Entries.AddRange(contentItemDocument.Entries);
                    return (DocumentIndex)document;
                }).ToArray()
                : builtContentDocuments.Cast<DocumentIndex>().ToArray();

            bool indexed = documentManager is LeanCorpusDocumentIndexManager leanCorpusDocuments
                ? await leanCorpusDocuments.AddDocumentsToEmptyIndexAsync(profile, documents).ConfigureAwait(false)
                : await documentManager.AddOrUpdateDocumentsAsync(profile, documents).ConfigureAwait(false);
            if (!indexed)
                return Results.Problem("The Orchard index provider could not ingest the performance corpus.", statusCode: StatusCodes.Status409Conflict);

            var taskPage = await tasks.GetIndexingTasksAsync(0, 10_000, "Content").ConfigureAwait(false);
            long lastTaskId = taskPage.Select(task => task.Id).DefaultIfEmpty(0).Max();
            await documentManager.SetLastTaskIdAsync(profile, lastTaskId).ConfigureAwait(false);
            double elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return Results.Ok(new RebuildSnapshot(elapsedMilliseconds, lastTaskId));
        });

        app.MapPost("/__test/performance/update/{id}", async (
            string id,
            [FromBody] PerformanceUpdateRequest request,
            [FromServices] IContentManager contentManager) =>
        {
            long started = Stopwatch.GetTimestamp();
            ContentItem? item = await contentManager.GetAsync(id, VersionOptions.DraftRequired).ConfigureAwait(false);
            if (item is null)
                return Results.NotFound();
            SetArticle(item, new TestArticleRequest(request.Title, request.Body));
            await contentManager.UpdateAsync(item).ConfigureAwait(false);
            if (!await contentManager.PublishAsync(item).ConfigureAwait(false))
                return Results.Problem("Orchard did not publish the performance update.", statusCode: StatusCodes.Status422UnprocessableEntity);

            double elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return Results.Ok(new PerformanceMutationSnapshot(elapsedMilliseconds));
        });

        app.MapGet("/__test/indexes/{provider}/{name}/cursor", async (
            string provider,
            string name,
            [FromServices] IIndexProfileManager profiles,
            [FromServices] IIndexingTaskManager tasks,
            IServiceProvider services) =>
        {
            IndexProfile? profile = await profiles.FindByNameAndProviderAsync(name, provider).ConfigureAwait(false);
            if (profile is null)
                return Results.NotFound();
            IDocumentIndexManager manager = services.GetRequiredKeyedService<IDocumentIndexManager>(provider);
            long cursor = await manager.GetLastTaskIdAsync(profile).ConfigureAwait(false);
            var firstPage = await tasks.GetIndexingTasksAsync(0, 1, "Content").ConfigureAwait(false);
            var pendingPage = await tasks.GetIndexingTasksAsync(cursor, 1, "Content").ConfigureAwait(false);
            return Results.Ok(new IndexingCursorSnapshot(cursor, firstPage.Any(), pendingPage.Any()));
        });

        app.MapPost("/__test/indexes/{provider}/{name}/rebuild", async (
            string provider,
            string name,
            [FromServices] IIndexProfileManager profiles,
            IServiceProvider services,
            [FromServices] ContentIndexingService indexing) =>
        {
            IndexProfile? profile = await profiles.FindByNameAndProviderAsync(name, provider).ConfigureAwait(false);
            if (profile is null)
                return Results.NotFound();
            long started = Stopwatch.GetTimestamp();
            IIndexManager manager = services.GetRequiredKeyedService<IIndexManager>(provider);
            if (!await manager.RebuildAsync(profile).ConfigureAwait(false))
                return Results.Problem("The Orchard index provider could not rebuild the requested profile.", statusCode: StatusCodes.Status409Conflict);
            await indexing.ProcessRecordsForAllIndexesAsync().ConfigureAwait(false);
            double elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return Results.Ok(new RebuildSnapshot(elapsedMilliseconds));
        });

        app.MapPost("/__test/indexes/{provider}/{name}/reset", async (
            string provider,
            string name,
            [FromServices] IIndexProfileManager profiles) =>
        {
            IndexProfile? profile = await profiles.FindByNameAndProviderAsync(name, provider).ConfigureAwait(false);
            if (profile is null)
                return Results.NotFound();
            await profiles.ResetAsync(profile).ConfigureAwait(false);
            return Results.Ok();
        });

        return app;
    }

    private static void SetArticle(ContentItem item, TestArticleRequest request)
    {
        item.DisplayText = request.Title;
        item.Owner = "admin";
        item.Author = "admin";
        item.ModifiedUtc = DateTime.UtcNow;
        item.Content["TitlePart"] = new JsonObject { ["Title"] = request.Title };
        item.Content["SearchTestArticle"] = new JsonObject
        {
            ["Title"] = new JsonObject { ["Text"] = request.Title },
            ["Body"] = new JsonObject { ["Text"] = request.Body },
            ["Category"] = new JsonObject { ["Text"] = request.Category },
            ["PublishedUtc"] = new JsonObject { ["Value"] = request.PublishedUtc },
            ["Sequence"] = new JsonObject { ["Value"] = request.Sequence },
            ["Rating"] = new JsonObject { ["Value"] = request.Rating },
            ["Featured"] = new JsonObject { ["Value"] = request.Featured },
            ["Location"] = new JsonObject { ["Latitude"] = 51.5m, ["Longitude"] = -0.1m },
        };
    }

    private static void SetPerformanceArticle(ContentItem item, TestArticleRequest request)
    {
        item.DisplayText = request.Title;
        item.Owner = "admin";
        item.Author = "admin";
        item.ModifiedUtc = DateTime.UtcNow;
        item.Content["TitlePart"] = new JsonObject { ["Title"] = request.Title };
        item.Content["SearchTestArticle"] = new JsonObject
        {
            ["Title"] = new JsonObject { ["Text"] = request.Title },
            ["Body"] = new JsonObject { ["Text"] = request.Body },
        };
    }

    public sealed record TestArticleRequest(
        string Title,
        string Body,
        string Category = "guides",
        string PublishedUtc = "2026-01-02T03:04:05Z",
        int Sequence = 3,
        double Rating = 4.25,
        bool Featured = true);

    public sealed record BulkArticleRequest(int Count, string SharedTerm);

    public sealed record BulkArticleResponse(ContentItemIdentity[] Items);

    public sealed record ContentItemIdentity(string ContentItemId, string ContentItemVersionId);

    public sealed record PerformanceBuildRequest(ContentItemIdentity[] Items);

    public sealed record PerformanceSearchRequest(string Term, int Start = 0, int Size = 10);

    public sealed record PerformanceUpdateRequest(string Title, string Body, string Term);

    public sealed record TestArticleResponse(string ContentItemId);

    public sealed record SearchSnapshot(bool Success, long TotalCount, string[] ContentItemIds);

    public sealed record IndexingCursorSnapshot(long LastTaskId, bool HasContentTasks, bool HasPendingContentTasks);

    public sealed record RebuildSnapshot(double ElapsedMilliseconds, long LastTaskId = 0);

    public sealed record PerformanceSearchSnapshot(
        double ElapsedMilliseconds,
        bool Success,
        long ReportedTotalCount,
        string[] ContentItemIds);

    public sealed record PerformanceMutationSnapshot(double ElapsedMilliseconds);

    public sealed record PerformanceProcessingSnapshot(double ElapsedMilliseconds);

}
