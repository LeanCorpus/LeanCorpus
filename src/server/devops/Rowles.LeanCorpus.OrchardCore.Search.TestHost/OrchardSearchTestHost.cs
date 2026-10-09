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
using OrchardCore.Queries;
using OrchardCore.Search.Abstractions;
using OrchardCore.Entities;
using OrchardCore.Lucene.Models;
using Rowles.LeanCorpus.OrchardCore.Search.Indexing;
using Rowles.LeanCorpus.OrchardCore.Search.TestModule;
using Rowles.LeanCorpus.OrchardCore.Search.TestModule.Indexing;
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

        app.MapPost("/__test/failures/{point}", (
            string point,
            [FromServices] IOrchardTestFailureControl control) =>
        {
            try
            {
                control.Arm(point);
                return Results.NoContent();
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(exception.Message);
            }
        });

        app.MapDelete("/__test/failures", ([FromServices] IOrchardTestFailureControl control) =>
        {
            control.Clear();
            return Results.NoContent();
        });

        app.MapGet("/__test/failures", ([FromServices] IOrchardTestFailureControl control) =>
            Results.Ok(new FailureSnapshot(control.LastTriggered)));

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

        app.MapPost("/__test/recovery/articles", async (
            [FromBody] TestArticleRequest request,
            [FromServices] IContentItemIdGenerator idGenerator,
            [FromServices] YesSqlSession session) =>
        {
            var item = new ContentItem { ContentType = "SearchTestArticle" };
            item.ContentItemId = idGenerator.GenerateUniqueId(item);
            item.ContentItemVersionId = idGenerator.GenerateUniqueId(item);
            SetArticle(item, request);
            item.Published = true;
            item.Latest = true;
            await session.SaveAsync(item).ConfigureAwait(false);
            await session.SaveChangesAsync().ConfigureAwait(false);
            return Results.Ok(new TestArticleResponse(item.ContentItemId));
        });

        app.MapPost("/__test/recovery/tasks/{recordId}/{taskType}", async (
            string recordId,
            string taskType,
            [FromServices] IIndexingTaskManager tasks,
            [FromServices] IContentManager contentManager,
            [FromServices] YesSqlSession session) =>
        {
            if (!Enum.TryParse(taskType, ignoreCase: true, out RecordIndexingTaskTypes parsed) || !Enum.IsDefined(parsed))
                return Results.BadRequest($"Unknown Orchard record indexing task type '{taskType}'.");

            if (parsed == RecordIndexingTaskTypes.Delete)
            {
                ContentItem? item = await contentManager.GetAsync(recordId, VersionOptions.Latest).ConfigureAwait(false);
                if (item is null)
                    return Results.NotFound();
                session.Delete(item);
            }

            await tasks.CreateTaskAsync(new CreateIndexingTaskContext(recordId, "Content", parsed)).ConfigureAwait(false);
            await session.SaveChangesAsync().ConfigureAwait(false);
            return Results.NoContent();
        });

        app.MapPost("/__test/recovery/process-delete/{provider}/{name}", async (
            string provider,
            string name,
            [FromServices] IIndexProfileManager profiles,
            [FromServices] IIndexingTaskManager tasks,
            IServiceProvider services) =>
        {
            IndexProfile? profile = await profiles.FindByNameAndProviderAsync(name, provider).ConfigureAwait(false);
            if (profile is null)
                return Results.NotFound();

            IDocumentIndexManager documents = services.GetRequiredKeyedService<IDocumentIndexManager>(provider);
            long cursor = await documents.GetLastTaskIdAsync(profile).ConfigureAwait(false);
            RecordIndexingTask? task = (await tasks.GetIndexingTasksAsync(cursor, 1, "Content").ConfigureAwait(false)).FirstOrDefault();
            if (task is null)
                return Results.Ok(new DeleteTaskProcessingSnapshot(false, false, cursor));
            if (task.Type != RecordIndexingTaskTypes.Delete)
                return Results.Conflict($"Expected an Orchard delete task after cursor {cursor}, found {task.Type}.");

            bool deleted = await documents.DeleteDocumentsAsync(profile, [task.RecordId]).ConfigureAwait(false);
            if (deleted)
                await documents.SetLastTaskIdAsync(profile, task.Id).ConfigureAwait(false);

            return Results.Ok(new DeleteTaskProcessingSnapshot(true, deleted, await documents.GetLastTaskIdAsync(profile).ConfigureAwait(false)));
        });

        app.MapPost("/__test/articles/bulk", async (
            [FromBody] BulkArticleRequest request,
            [FromServices] IContentItemIdGenerator idGenerator,
            [FromServices] YesSqlSession session) =>
        {
            if (request.Count is < 1 or > 2000 || string.IsNullOrWhiteSpace(request.SharedTerm))
                return Results.BadRequest("Bulk content requires 1 to 2,000 items and a shared search term.");

            var contentItems = new ContentItem[request.Count];
            var corpusRandom = new Random(3901);
            long setupStarted = Stopwatch.GetTimestamp();
            Console.WriteLine($"Generating {request.Count} deterministic SearchTestArticle records.");
            for (int index = 0; index < request.Count; index++)
            {
                var item = new ContentItem { ContentType = "SearchTestArticle" };
                item.ContentItemId = idGenerator.GenerateUniqueId(item);
                SetPerformanceArticle(item, request.SharedTerm, index, corpusRandom.Next());
                item.ContentItemVersionId = idGenerator.GenerateUniqueId(item);
                item.Published = true;
                item.Latest = true;
                contentItems[index] = item;
            }
            Console.WriteLine($"Generated the performance corpus after {Stopwatch.GetElapsedTime(setupStarted).TotalSeconds:F1} seconds; persisting Orchard content.");

            // Persist the deterministic corpus once; measured builds queue ordinary Orchard
            // indexing tasks after each provider rebuild.
            for (int index = 0; index < contentItems.Length; index++)
            {
                ContentItem item = contentItems[index];
                await session.SaveAsync(item).ConfigureAwait(false);
                if ((index + 1) % 500 == 0 || index + 1 == contentItems.Length)
                {
                    await session.SaveChangesAsync().ConfigureAwait(false);
                    Console.WriteLine($"Committed {index + 1}/{contentItems.Length} performance content items after {Stopwatch.GetElapsedTime(setupStarted).TotalSeconds:F1} seconds.");
                }
            }
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

        app.MapPost("/__test/articles/{id}/unpublish", async (string id, [FromServices] IContentManager contentManager) =>
        {
            ContentItem? item = await contentManager.GetAsync(id, VersionOptions.Latest).ConfigureAwait(false);
            if (item is null)
                return Results.NotFound();
            return await contentManager.UnpublishAsync(item).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.Problem("Orchard did not unpublish the SearchTestArticle item.", statusCode: StatusCodes.Status422UnprocessableEntity);
        });

        app.MapDelete("/__test/articles/{id}", async (string id, [FromServices] IContentManager contentManager) =>
        {
            ContentItem? item = await contentManager.GetAsync(id, VersionOptions.Latest).ConfigureAwait(false);
            if (item is null)
                return Results.NotFound();

            await contentManager.RemoveAsync(item).ConfigureAwait(false);
            return Results.NoContent();
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

        app.MapPost("/__test/query/{provider}/{name}", async (
            string provider,
            string name,
            [FromBody] ProviderQueryRequest request,
            [FromServices] IEnumerable<IQuerySource> querySources) =>
        {
            IQuerySource? querySource = querySources.SingleOrDefault(source => source.Name == provider);
            if (querySource is null)
                return Results.NotFound();

            var query = new Query { Source = provider };
            if (provider.Equals("LeanCorpus", StringComparison.Ordinal))
            {
                query.Schema = request.LeanCorpusSchema;
            }
            else if (provider.Equals("Lucene", StringComparison.Ordinal))
            {
                query.Put("LuceneQueryMetadata", new LuceneQueryMetadata
                {
                    Index = name,
                    Template = request.LuceneTemplate,
                });
            }
            else
            {
                return Results.BadRequest($"The provider '{provider}' is not supported by this query probe.");
            }

            IQueryResults results = await querySource.ExecuteQueryAsync(query, new Dictionary<string, object>()).ConfigureAwait(false);
            object[] items = results.Items.ToArray();
            string[] ids = items.Select(ReadQueryResultId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .ToArray();
            string[] storedValues = request.StoredField is null
                ? []
                : items.SelectMany(item => ReadQueryResultField(item, request.StoredField)).ToArray();
            return Results.Ok(new ProviderQuerySnapshot(ids, storedValues));
        });

        app.MapGet("/__test/field-index-handlers", ([FromServices] IEnumerable<IContentFieldIndexHandler> handlers) =>
            Results.Ok(handlers.Select(handler => handler.GetType().Name).Order(StringComparer.Ordinal).ToArray()));

        app.MapPost("/__test/vector-probe/{provider}/{name}", async (
            string provider,
            string name,
            [FromServices] IIndexProfileManager profiles,
            [FromServices] IContentItemIdGenerator idGenerator,
            [FromServices] IIndexingTaskManager tasks,
            [FromServices] YesSqlSession session) =>
        {
            if (!provider.Equals("LeanCorpus", StringComparison.Ordinal))
                return Results.BadRequest("The Orchard vector probe runs against the LeanCorpus provider.");

            IndexProfile? profile = await profiles.FindByNameAndProviderAsync(name, provider).ConfigureAwait(false);
            if (profile is null)
                return Results.NotFound();

            var item = new ContentItem { ContentType = VectorProbeIndexHandler.ContentType };
            item.ContentItemId = idGenerator.GenerateUniqueId(item);
            item.ContentItemVersionId = idGenerator.GenerateUniqueId(item);
            item.DisplayText = "Orchard vector probe";
            item.Published = true;
            item.Latest = true;
            item.Content["TitlePart"] = new JsonObject { ["Title"] = item.DisplayText };
            await session.SaveAsync(item).ConfigureAwait(false);
            await tasks.CreateTaskAsync(new CreateIndexingTaskContext(
                item.ContentItemId, "Content", RecordIndexingTaskTypes.Update)).ConfigureAwait(false);
            await session.SaveChangesAsync().ConfigureAwait(false);

            return Results.Ok(new VectorProbeSnapshot(item.ContentItemId, VectorProbeIndexHandler.Dimensions, []));
        });

        app.MapPost("/__test/vector-probe/{provider}/{name}/{contentItemId}", async (
            string provider,
            string name,
            string contentItemId,
            [FromServices] IIndexProfileManager profiles,
            [FromServices] ContentIndexingService indexing,
            [FromServices] IEnumerable<IQuerySource> querySources) =>
        {
            if (!provider.Equals("LeanCorpus", StringComparison.Ordinal))
                return Results.BadRequest("The Orchard vector probe runs against the LeanCorpus provider.");

            IndexProfile? profile = await profiles.FindByNameAndProviderAsync(name, provider).ConfigureAwait(false);
            if (profile is null)
                return Results.NotFound();

            // This is a separate request from publication, so Orchard has committed the task
            // before the test explicitly replays it and observes the native query result.
            await indexing.ProcessRecordsAsync([profile.Id]).ConfigureAwait(false);
            return Results.Ok(await QueryVectorAsync(provider, name, contentItemId, querySources).ConfigureAwait(false));
        });

        app.MapGet("/__test/vector-probe/query/{provider}/{name}/{contentItemId}", async (
            string provider,
            string name,
            string contentItemId,
            [FromServices] IIndexProfileManager profiles,
            [FromServices] IEnumerable<IQuerySource> querySources) =>
        {
            if (!provider.Equals("LeanCorpus", StringComparison.Ordinal))
                return Results.BadRequest("The Orchard vector probe runs against the LeanCorpus provider.");
            if (await profiles.FindByNameAndProviderAsync(name, provider).ConfigureAwait(false) is null)
                return Results.NotFound();

            return Results.Ok(await QueryVectorAsync(provider, name, contentItemId, querySources).ConfigureAwait(false));
        });

        app.MapPost("/__test/indexing/process", async (IServiceProvider services) =>
        {
            ContentIndexingService? indexing = services.GetService<ContentIndexingService>();
            if (indexing is null)
                return Results.Problem("Orchard did not register its content indexing service.", statusCode: StatusCodes.Status503ServiceUnavailable);
            long started = Stopwatch.GetTimestamp();
            await indexing.ProcessRecordsForAllIndexesAsync().ConfigureAwait(false);
            return Results.Ok(new PerformanceProcessingSnapshot(Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        });

        app.MapPost("/__test/indexing/process/{provider}/{name}", async (
            string provider,
            string name,
            [FromServices] IIndexProfileManager profiles,
            [FromServices] ContentIndexingService indexing,
            IServiceProvider services) =>
        {
            IndexProfile? profile = await profiles.FindByNameAndProviderAsync(name, provider).ConfigureAwait(false);
            if (profile is null)
                return Results.NotFound();

            IDocumentIndexManager documentManager = services.GetRequiredKeyedService<IDocumentIndexManager>(provider);
            long started = Stopwatch.GetTimestamp();
            await indexing.ProcessRecordsAsync([profile.Id]).ConfigureAwait(false);
            double elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long lastTaskId = await documentManager.GetLastTaskIdAsync(profile).ConfigureAwait(false);
            return Results.Ok(new PerformanceProcessingSnapshot(elapsedMilliseconds, lastTaskId));
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

        app.MapPost("/__test/performance/tasks", async (
            [FromBody] PerformanceTaskRequest request,
            [FromServices] IIndexingTaskManager tasks,
            [FromServices] YesSqlSession session) =>
        {
            if (request.ContentItemIds.Length != 2000
                || request.ContentItemIds.Distinct(StringComparer.Ordinal).Count() != request.ContentItemIds.Length)
                return Results.BadRequest("A performance task set must contain 2,000 distinct content item IDs.");

            for (int index = 0; index < request.ContentItemIds.Length; index++)
            {
                string id = request.ContentItemIds[index];
                await tasks.CreateTaskAsync(new CreateIndexingTaskContext(id, "Content", RecordIndexingTaskTypes.Update)).ConfigureAwait(false);
                if ((index + 1) % 500 == 0 || index + 1 == request.ContentItemIds.Length)
                    await session.SaveChangesAsync().ConfigureAwait(false);
            }

            return Results.Ok();
        });

        app.MapPost("/__test/performance/rebuild/{provider}/{name}", async (
            string provider,
            string name,
            [FromServices] IIndexProfileManager profiles,
            IServiceProvider services) =>
        {
            IndexProfile? profile = await profiles.FindByNameAndProviderAsync(name, provider).ConfigureAwait(false);
            if (profile is null)
                return Results.NotFound();

            long started = Stopwatch.GetTimestamp();
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            IIndexManager indexManager = services.GetRequiredKeyedService<IIndexManager>(provider);
            if (!await indexManager.RebuildAsync(profile).ConfigureAwait(false))
                return Results.Problem("The Orchard index provider could not rebuild the requested performance profile.", statusCode: StatusCodes.Status409Conflict);

            double elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long allocatedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore);
            return Results.Ok(new PerformanceRebuildSnapshot(elapsedMilliseconds, allocatedBytes));
        });

        app.MapPost("/__test/performance/process/{provider}/{name}", async (
            string provider,
            string name,
            [FromBody] PerformanceBuildRequest request,
            [FromServices] IIndexProfileManager profiles,
            [FromServices] IIndexingTaskManager tasks,
            [FromServices] IEnumerable<ISearchService> searchServices,
            [FromServices] ContentIndexingService indexing,
            IServiceProvider services) =>
        {
            IndexProfile? profile = await profiles.FindByNameAndProviderAsync(name, provider).ConfigureAwait(false);
            if (profile is null || request.ExpectedDocumentCount != 2000
                || request.ContentItemIds.Length != request.ExpectedDocumentCount
                || string.IsNullOrWhiteSpace(request.SharedTerm))
                return Results.NotFound();

            IDocumentIndexManager documentManager = services.GetRequiredKeyedService<IDocumentIndexManager>(provider);
            long previousTaskId = await documentManager.GetLastTaskIdAsync(profile).ConfigureAwait(false);
            RecordIndexingTask[] pendingTasks = (await tasks.GetIndexingTasksAsync(
                    previousTaskId, request.ExpectedDocumentCount + 1, "Content").ConfigureAwait(false))
                .ToArray();
            HashSet<string> expectedIds = request.ContentItemIds.ToHashSet(StringComparer.Ordinal);
            if (pendingTasks.Length != request.ExpectedDocumentCount
                || !expectedIds.SetEquals(pendingTasks.Select(task => task.RecordId)))
                return Results.Problem(
                    $"The Orchard task stream did not contain exactly {request.ExpectedDocumentCount} performance records for {provider}.",
                    statusCode: StatusCodes.Status409Conflict);

            long started = Stopwatch.GetTimestamp();
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            await indexing.ProcessRecordsAsync([profile.Id]).ConfigureAwait(false);
            long lastTaskId = await documentManager.GetLastTaskIdAsync(profile).ConfigureAwait(false);
            if (lastTaskId != pendingTasks.Max(task => task.Id))
                return Results.Problem("Orchard did not advance the provider cursor through the complete performance task set.", statusCode: StatusCodes.Status409Conflict);
            double elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long allocatedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore);
            long workingSetBytes = Process.GetCurrentProcess().WorkingSet64;
            long indexSizeBytes = GetProviderIndexSizeBytes(app.Environment.ContentRootPath, provider);
            ISearchService? searchService = searchServices.SingleOrDefault(service => service.Name == provider);
            if (searchService is null)
                return Results.NotFound();

            // Each SearchAsync call already scans and materialises every matching stored ID.
            // Verify Lucene's full returned ID set in one call; LeanCorpus reports an exact
            // total and caps public pages at 100, so validate that count and one complete page.
            // This avoids repeating the full stored-field scan once per page.
            bool isLucene = provider.Equals("Lucene", StringComparison.Ordinal);
            int requestedSize = isLucene ? request.ExpectedDocumentCount : 100;
            var page = await searchService.SearchAsync(profile, request.SharedTerm, 0, requestedSize).ConfigureAwait(false);
            var foundContentItemIds = page.ContentItemIds.ToHashSet(StringComparer.Ordinal);
            bool invalidPage = !page.Success
                || page.ContentItemIds.Count != requestedSize
                || foundContentItemIds.Count != requestedSize
                || page.ContentItemIds.Any(id => !expectedIds.Contains(id));
            if (invalidPage || (!isLucene && page.TotalCount != request.ExpectedDocumentCount))
                return Results.Problem(
                    $"The {provider} performance search returned an incomplete or unexpected result set: success={page.Success}, pageCount={page.ContentItemIds.Count}, totalCount={page.TotalCount}, expectedCount={request.ExpectedDocumentCount}.",
                    statusCode: StatusCodes.Status409Conflict);

            long searchableDocumentCount = isLucene ? foundContentItemIds.Count : page.TotalCount;

            return Results.Ok(new RebuildSnapshot(
                elapsedMilliseconds, lastTaskId, allocatedBytes, workingSetBytes, indexSizeBytes, searchableDocumentCount));
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

        app.MapPost("/__test/performance/update-batch", async (
            [FromBody] PerformanceBatchMutationRequest request,
            [FromServices] IContentManager contentManager) =>
        {
            long started = Stopwatch.GetTimestamp();
            foreach (string id in request.ContentItemIds)
            {
                ContentItem? item = await contentManager.GetAsync(id, VersionOptions.DraftRequired).ConfigureAwait(false);
                if (item is null)
                    return Results.NotFound($"Performance article '{id}' was not found.");

                SetArticle(item, new TestArticleRequest(
                    $"Orchard performance batch {request.Term}",
                    $"{request.SharedTerm} orchard deterministic batch update {request.Term} with Unicode Crème 東京 🚀",
                    Keywords: request.Term));
                await contentManager.UpdateAsync(item).ConfigureAwait(false);
                if (!await contentManager.PublishAsync(item).ConfigureAwait(false))
                    return Results.Problem("Orchard did not publish the performance batch update.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            return Results.Ok(new PerformanceMutationSnapshot(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                request.ContentItemIds.Length));
        });

        app.MapPost("/__test/performance/delete-batch", async (
            [FromBody] PerformanceBatchMutationRequest request,
            [FromServices] IContentManager contentManager) =>
        {
            long started = Stopwatch.GetTimestamp();
            foreach (string id in request.ContentItemIds)
            {
                ContentItem? item = await contentManager.GetAsync(id, VersionOptions.Latest).ConfigureAwait(false);
                if (item is null)
                    return Results.NotFound($"Performance article '{id}' was not found.");
                if (!await contentManager.UnpublishAsync(item).ConfigureAwait(false))
                    return Results.Problem("Orchard did not unpublish a performance article.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            return Results.Ok(new PerformanceMutationSnapshot(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                request.ContentItemIds.Length));
        });

        app.MapGet("/__test/performance/open/{provider}/{name}", async (
            string provider,
            string name,
            [FromServices] IIndexProfileManager profiles,
            IServiceProvider services) =>
        {
            IndexProfile? profile = await profiles.FindByNameAndProviderAsync(name, provider).ConfigureAwait(false);
            if (profile is null)
                return Results.NotFound();

            IIndexManager indexManager = services.GetRequiredKeyedService<IIndexManager>(provider);
            long started = Stopwatch.GetTimestamp();
            bool exists = await indexManager.ExistsAsync(profile.IndexFullName).ConfigureAwait(false);
            return Results.Ok(new PerformanceIndexOpenSnapshot(Stopwatch.GetElapsedTime(started).TotalMilliseconds, exists));
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
            RecordIndexingTask[] allTasks = (await tasks.GetIndexingTasksAsync(0, 10_000, "Content").ConfigureAwait(false)).ToArray();
            var firstPage = await tasks.GetIndexingTasksAsync(0, 1, "Content").ConfigureAwait(false);
            var pendingPage = await tasks.GetIndexingTasksAsync(cursor, 1, "Content").ConfigureAwait(false);
            long latestTaskId = allTasks.Length == 0 ? 0 : allTasks.Max(task => task.Id);
            return Results.Ok(new IndexingCursorSnapshot(cursor, firstPage.Any(), pendingPage.Any(), latestTaskId));
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
            ["Keywords"] = new JsonObject { ["Text"] = request.Keywords },
            ["PublishedUtc"] = new JsonObject { ["Value"] = request.PublishedUtc },
            ["Sequence"] = new JsonObject { ["Value"] = request.Sequence },
            ["Rating"] = new JsonObject { ["Value"] = request.Rating },
            ["Featured"] = new JsonObject { ["Value"] = request.Featured },
            ["Location"] = new JsonObject { ["Latitude"] = 51.5m, ["Longitude"] = -0.1m },
        };
    }

    private static void SetPerformanceArticle(ContentItem item, string sharedTerm, int index, int randomValue)
    {
        string unicode = index % 2 == 0 ? "Crème 東京 🚀" : "e\u0301 Καλημέρα 𐐷";
        string publishedUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddDays(index % 2000)
            .ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        string title = $"Article {index:D4} {unicode}";
        string body = $"{sharedTerm} orchard deterministic performance document {index:D4}.\n\n" +
            $"This is a multi-paragraph body for the seeded Orchard search corpus. It contains {unicode}.\n\n" +
            "The final paragraph repeats orchard and deterministic so term and multi-term searches have stable matches.";
        item.DisplayText = title;
        item.Owner = "admin";
        item.Author = "admin";
        item.ModifiedUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(index % 2000);
        item.Content["TitlePart"] = new JsonObject { ["Title"] = title };
        item.Content["SearchTestArticle"] = new JsonObject
        {
            ["Title"] = new JsonObject { ["Text"] = title },
            ["Body"] = new JsonObject { ["Text"] = body },
            ["Category"] = new JsonObject { ["Text"] = $"guide-{index % 20:D2}" },
            ["Keywords"] = new JsonObject { ["Text"] = $"keyword-{index % 24:D2} orchard searchable" },
            ["PublishedUtc"] = new JsonObject { ["Value"] = publishedUtc },
            ["Sequence"] = new JsonObject { ["Value"] = randomValue % 100_000 },
            ["Rating"] = new JsonObject { ["Value"] = (randomValue % 10_000) / 100d },
        };
    }

    public sealed record TestArticleRequest(
        string Title,
        string Body,
        string Category = "guides",
        string PublishedUtc = "2026-01-02T03:04:05Z",
        int Sequence = 3,
        double Rating = 4.25,
        bool Featured = true,
        string Keywords = "orchard search");

    public sealed record BulkArticleRequest(int Count, string SharedTerm);

    public sealed record BulkArticleResponse(ContentItemIdentity[] Items);

    public sealed record ContentItemIdentity(string ContentItemId, string ContentItemVersionId);

    public sealed record PerformanceTaskRequest(string[] ContentItemIds);

    public sealed record PerformanceBuildRequest(int ExpectedDocumentCount, string[] ContentItemIds, string SharedTerm);

    public sealed record PerformanceRebuildSnapshot(double ElapsedMilliseconds, long AllocatedBytes);

    public sealed record PerformanceSearchRequest(string Term, int Start = 0, int Size = 10);

    public sealed record PerformanceUpdateRequest(string Title, string Body, string Term);

    public sealed record PerformanceBatchMutationRequest(string[] ContentItemIds, string SharedTerm, string Term);

    public sealed record TestArticleResponse(string ContentItemId);

    public sealed record SearchSnapshot(bool Success, long TotalCount, string[] ContentItemIds);

    public sealed record ProviderQueryRequest(string LeanCorpusSchema, string LuceneTemplate, string? StoredField = null);

    public sealed record ProviderQuerySnapshot(string[] ContentItemIds, string[] StoredFieldValues);

    public static string FormatLuceneDate(DateTimeOffset value)
        => Lucene.Net.Documents.DateTools.DateToString(value.UtcDateTime, Lucene.Net.Documents.DateResolution.MILLISECOND);

    public sealed record IndexingCursorSnapshot(
        long LastTaskId,
        bool HasContentTasks,
        bool HasPendingContentTasks,
        long LatestTaskId);

    public sealed record DeleteTaskProcessingSnapshot(bool FoundTask, bool Succeeded, long LastTaskId);

    public sealed record RebuildSnapshot(
        double ElapsedMilliseconds,
        long LastTaskId = 0,
        long AllocatedBytes = 0,
        long WorkingSetBytes = 0,
        long IndexSizeBytes = 0,
        long SearchableDocumentCount = 0);

    public sealed record PerformanceSearchSnapshot(
        double ElapsedMilliseconds,
        bool Success,
        long ReportedTotalCount,
        string[] ContentItemIds);

    public sealed record PerformanceMutationSnapshot(double ElapsedMilliseconds, int ItemCount = 1);

    public sealed record PerformanceProcessingSnapshot(double ElapsedMilliseconds, long LastTaskId = 0);

    public sealed record PerformanceIndexOpenSnapshot(double ElapsedMilliseconds, bool Exists);

    public sealed record VectorProbeSnapshot(string ContentItemId, int Dimensions, string[] ResultIds);

    public sealed record FailureSnapshot(string? LastTriggered);

    private static async Task<VectorProbeSnapshot> QueryVectorAsync(
        string provider,
        string name,
        string contentItemId,
        IEnumerable<IQuerySource> querySources)
    {
        IQuerySource? querySource = querySources.SingleOrDefault(source => source.Name == provider);
        if (querySource is null)
            throw new InvalidOperationException($"Orchard did not register the '{provider}' query source.");

        string querySchema = $$"""
            {"index":{{System.Text.Json.JsonSerializer.Serialize(name)}},"query":{"type":"vectorKnn","field":{{System.Text.Json.JsonSerializer.Serialize(VectorProbeIndexHandler.FieldName)}},"value":[1,0,0],"topK":1},"take":1}
            """;
        OrchardCore.Queries.Query query = new() { Source = provider, Schema = querySchema };
        IQueryResults results = await querySource.ExecuteQueryAsync(query, new Dictionary<string, object>()).ConfigureAwait(false);
        string[] resultIds = results.Items
            .Select(result => result.GetType().GetProperty("Id")?.GetValue(result) as string)
            .Where(id => id is not null)
            .Cast<string>()
            .ToArray();
        return new VectorProbeSnapshot(contentItemId, VectorProbeIndexHandler.Dimensions, resultIds);
    }

    private static string? ReadQueryResultId(object result)
    {
        if (result is JsonObject json)
            return json["ContentItemId"]?.GetValue<string>()
                ?? json["contentItemId"]?.GetValue<string>()
                ?? json["Id"]?.GetValue<string>()
                ?? json["id"]?.GetValue<string>();

        if (result is IDictionary<string, object> dictionary)
        {
            foreach (string name in new[] { "ContentItemId", "contentItemId", "Id", "id" })
            {
                if (dictionary.TryGetValue(name, out object? value) && value is string id)
                    return id;
            }
        }

        return result.GetType().GetProperty("Id")?.GetValue(result) as string
            ?? result.GetType().GetProperty("ContentItemId")?.GetValue(result) as string;
    }

    private static IEnumerable<string> ReadQueryResultField(object result, string fieldName)
    {
        object? fields = result.GetType().GetProperty("Fields")?.GetValue(result);
        if (fields is IReadOnlyDictionary<string, IReadOnlyList<string>> readOnly
            && readOnly.TryGetValue(fieldName, out IReadOnlyList<string>? values))
            return values;

        if (fields is System.Collections.IDictionary dictionary && dictionary.Contains(fieldName))
            return FlattenFieldValue(dictionary[fieldName]);

        System.Reflection.PropertyInfo? indexer = result.GetType().GetProperty("Item", [typeof(string)]);
        object? value = indexer?.GetValue(result, [fieldName])
            ?? result.GetType().GetProperty(fieldName)?.GetValue(result);
        return FlattenFieldValue(value);
    }

    private static IEnumerable<string> FlattenFieldValue(object? value)
    {
        if (value is null)
            return [];
        if (value is string text)
            return [text];
        if (value is System.Collections.IEnumerable enumerable)
            return enumerable.Cast<object?>().Where(item => item is not null).Select(item => item!.ToString()!);
        return [value.ToString()!];
    }

    private static long GetProviderIndexSizeBytes(string contentRoot, string provider)
    {
        string? storagePath;
        if (provider.Equals("LeanCorpus", StringComparison.Ordinal))
        {
            string? indexesDirectory = Directory.EnumerateDirectories(contentRoot, "indexes", SearchOption.AllDirectories)
                .FirstOrDefault(path => Path.GetFileName(Path.GetDirectoryName(path)) == "LeanCorpus");
            storagePath = indexesDirectory is null ? null : Path.GetDirectoryName(indexesDirectory);
        }
        else
        {
            storagePath = Directory.EnumerateFiles(contentRoot, "segments_*", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .FirstOrDefault(path => path is not null
                    && !path.Contains($"{Path.DirectorySeparatorChar}LeanCorpus{Path.DirectorySeparatorChar}indexes{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        }

        return storagePath is null || !Directory.Exists(storagePath)
            ? 0
            : Directory.EnumerateFiles(storagePath, "*", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path).Length)
                .Sum();
    }

}
