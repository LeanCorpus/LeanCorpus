using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Playwright;

namespace Rowles.LeanCorpus.OrchardCore.Search.FunctionalTests;

[Trait("Area", "Functional")]
[Collection(OrchardBrowserTestCollection.Name)]
public sealed class ProviderVectorProbeTests
{
    [Fact]
    public async Task OrchardPartHandlerIndexesThreeDimensionalVectorForNativeKnnQuery()
    {
        string contentRoot = Path.Combine(Path.GetTempPath(), "leancorpus-orchard-vector-probe", Guid.NewGuid().ToString("N"));
        (WebApplication app, Uri address) = await OrchardSearchTestHost.StartIsolatedAsync(contentRoot);
        using var http = new HttpClient { BaseAddress = address };
        bool completed = false;
        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            IPage page = await browser.NewPageAsync();
            await SearchLifecycleBrowserTests.InstallTestSiteAsync(page, address);

            using HttpResponseMessage created = await http.PostAsync(
                "/__test/vector-probe/LeanCorpus/Search-LeanCorpus",
                content: null,
                cancellationToken: TestContext.Current.CancellationToken);
            created.EnsureSuccessStatusCode();
            OrchardSearchTestHost.VectorProbeSnapshot createdProbe = await created.Content
                .ReadFromJsonAsync<OrchardSearchTestHost.VectorProbeSnapshot>(TestContext.Current.CancellationToken)
                ?? throw new InvalidOperationException("Orchard returned no vector probe creation result.");

            using HttpResponseMessage queried = await http.PostAsync(
                $"/__test/vector-probe/LeanCorpus/Search-LeanCorpus/{Uri.EscapeDataString(createdProbe.ContentItemId)}",
                content: null,
                cancellationToken: TestContext.Current.CancellationToken);
            queried.EnsureSuccessStatusCode();
            OrchardSearchTestHost.VectorProbeSnapshot probe = await queried.Content
                .ReadFromJsonAsync<OrchardSearchTestHost.VectorProbeSnapshot>(TestContext.Current.CancellationToken)
                ?? throw new InvalidOperationException("Orchard returned no vector probe query result.");

            Assert.Equal(3, probe.Dimensions);
            Assert.Equal(createdProbe.ContentItemId, probe.ContentItemId);
            Assert.Equal(createdProbe.ContentItemId, Assert.Single(probe.ResultIds));
            completed = true;
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
            if (completed && Directory.Exists(contentRoot))
                Directory.Delete(contentRoot, recursive: true);
            else if (Directory.Exists(contentRoot))
                Console.WriteLine($"Orchard vector probe content root retained at {contentRoot}");
        }
    }
}
