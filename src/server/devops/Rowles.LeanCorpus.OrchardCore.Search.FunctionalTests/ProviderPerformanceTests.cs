using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Playwright;

namespace Rowles.LeanCorpus.OrchardCore.Search.FunctionalTests;

[Trait("Area", "Performance")]
public sealed class ProviderPerformanceTests
{
    private const int DocumentCount = 2000;
    private const int WarmupCount = 3;
    private const int MeasuredCount = 10;
    private const string SharedTerm = "orchardperfshared";
    private static readonly string[] Providers = ["Lucene", "LeanCorpus"];
    private static readonly string[] SampleWords =
    [
        "amber", "bravo", "copper", "delta", "ember", "fable", "garden",
        "harbor", "indigo", "juniper", "kestrel", "lantern", "meadow", "nectar",
    ];

    [Fact(Explicit = true)]
    public async Task ProviderPerformanceRunsPersistRawSamplesAndEnvironment()
    {
        string repositoryRoot = FindRepositoryRoot();
        string outputPath = Environment.GetEnvironmentVariable("LEANCORE_ORCHARD_PERF_OUTPUT")
            ?? Path.Combine(repositoryRoot, "docs", "server", "evidence", "orchard-core-search-provider-runs.json");
        outputPath = Path.GetFullPath(outputPath);
        string contentRoot = Path.Combine(Path.GetTempPath(), "leancorpus-orchard-performance", Guid.NewGuid().ToString("N"));
        var report = new ProviderPerformanceReport
        {
            StartedUtc = DateTimeOffset.UtcNow,
            OutputPath = outputPath,
            TargetDocumentCount = DocumentCount,
            Environment = await CaptureEnvironmentAsync(repositoryRoot).ConfigureAwait(false),
            MeasurementNotes =
            [
                "Cold search is the first provider query after the final measured rebuild; operating-system caches are not flushed.",
                "Search timings measure ISearchService.SearchAsync inside the test host and exclude loopback transport time.",
                "Update-to-index timings sum server-side content read, draft update and publish, Orchard task processing for all enabled providers, and a target-provider visibility query; task processing runs in a separate request after the content transaction commits.",
                "Each Orchard content task is consumed by both enabled provider profiles, so update-to-index timings include both providers processing the task.",
                "The explicit corpus consists of fully populated published Orchard ContentItems persisted through YesSQL without per-item lifecycle handlers or task creation, isolating provider build timings from corpus setup cost.",
                "The performance corpus uses the required Title and Body fields; broader Orchard field types are covered by provider integration tests.",
                "Full-build and rebuild timings include physical index replacement, Orchard content-item retrieval, provider-neutral document construction through Orchard content-part and field handlers, one provider batch write, and cursor advancement.",
                "Full-build and rebuild timings bypass per-record Orchard task-log replay and the provider-specific ContentItemDocumentIndex refresh path so both providers receive the same handler-built 2,000-document batch. LeanCorpus appends the batch to its newly rebuilt empty index; task-log replay, content refresh and cursor recovery are covered by the functional test suite.",
            ],
        };

        WebApplication? app = null;
        bool completed = false;
        try
        {
            (WebApplication startedApp, Uri address) = await OrchardSearchTestHost.StartIsolatedAsync(contentRoot).ConfigureAwait(false);
            app = startedApp;
            using var http = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromMinutes(5) };
            using IPlaywright playwright = await Playwright.CreateAsync().ConfigureAwait(false);
            await using IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }).ConfigureAwait(false);
            IPage page = await browser.NewPageAsync().ConfigureAwait(false);
            await SearchLifecycleBrowserTests.InstallTestSiteAsync(page, address).ConfigureAwait(false);

            Console.WriteLine($"Seeding {DocumentCount} Orchard performance items.");
            using HttpResponseMessage bulkResponse = await http.PostAsJsonAsync(
                "/__test/articles/bulk",
                new OrchardSearchTestHost.BulkArticleRequest(DocumentCount, SharedTerm),
                TestContext.Current.CancellationToken).ConfigureAwait(false);
            bulkResponse.EnsureSuccessStatusCode();
            OrchardSearchTestHost.BulkArticleResponse bulk = await bulkResponse.Content
                .ReadFromJsonAsync<OrchardSearchTestHost.BulkArticleResponse>(TestContext.Current.CancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("Orchard returned no bulk article IDs.");
            Assert.Equal(DocumentCount, bulk.Items.Length);
            Console.WriteLine($"Seeded {bulk.Items.Length} Orchard performance items.");

            foreach (string provider in Providers)
            {
                string profile = GetProfileName(provider);
                Console.WriteLine($"Starting initial {provider} build.");
                OrchardSearchTestHost.RebuildSnapshot build = await BuildAsync(http, provider, profile, bulk).ConfigureAwait(false);
                Console.WriteLine($"Completed initial {provider} build in {build.ElapsedMilliseconds:F2} ms.");
                report.InitialBuilds.Add(new InitialBuildRun(provider, build.ElapsedMilliseconds, build.LastTaskId));
                await AssertCursorDrainedAsync(http, provider, profile).ConfigureAwait(false);
            }

            foreach (string provider in Providers)
            {
                for (int iteration = 1; iteration <= 3; iteration++)
                {
                    string profile = GetProfileName(provider);
                    Console.WriteLine($"Starting {provider} rebuild {iteration}/3.");
                    OrchardSearchTestHost.RebuildSnapshot rebuild = await BuildAsync(http, provider, profile, bulk).ConfigureAwait(false);
                    Console.WriteLine($"Completed {provider} rebuild {iteration}/3 in {rebuild.ElapsedMilliseconds:F2} ms.");
                    report.Rebuilds.Add(new RebuildRun(provider, iteration, rebuild.ElapsedMilliseconds, rebuild.LastTaskId));
                    await AssertCursorDrainedAsync(http, provider, profile).ConfigureAwait(false);
                }
            }

            foreach (string provider in Providers)
            {
                string profile = GetProfileName(provider);
                foreach ((string phase, int iteration) in GetSamplePlan())
                {
                    using HttpResponseMessage response = await http.PostAsJsonAsync(
                        $"/__test/performance/search/{provider}/{profile}",
                        new OrchardSearchTestHost.PerformanceSearchRequest(SharedTerm, 0, 10),
                        TestContext.Current.CancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    OrchardSearchTestHost.PerformanceSearchSnapshot snapshot = await response.Content
                        .ReadFromJsonAsync<OrchardSearchTestHost.PerformanceSearchSnapshot>(TestContext.Current.CancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidOperationException($"Orchard returned no {provider} search measurement.");
                    report.SearchSamples.Add(new LatencySample(
                        provider,
                        phase,
                        iteration,
                        snapshot.ElapsedMilliseconds,
                        snapshot.Success,
                        snapshot.ContentItemIds.Length,
                        snapshot.ReportedTotalCount));
                    Assert.True(snapshot.Success, $"{provider} search failed for the shared performance term.");
                    Assert.Equal(10, snapshot.ContentItemIds.Length);
                }
            }

            foreach (string provider in Providers)
            {
                string profile = GetProfileName(provider);
                string providerSlug = provider.Equals("Lucene", StringComparison.Ordinal) ? "lucene" : "leancorpus";
                int sampleNumber = 0;
                foreach ((string phase, int iteration) in GetSamplePlan())
                {
                    string term = $"orchardupdate{providerSlug}{SampleWords[sampleNumber]}";
                    var update = new OrchardSearchTestHost.PerformanceUpdateRequest(
                        $"Orchard performance update {term}",
                        $"{SharedTerm} {term} updated orchard performance content",
                        term);
                    using HttpResponseMessage response = await http.PostAsJsonAsync(
                        $"/__test/performance/update/{bulk.Items[0].ContentItemId}",
                        update,
                        TestContext.Current.CancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    OrchardSearchTestHost.PerformanceMutationSnapshot mutation = await response.Content
                        .ReadFromJsonAsync<OrchardSearchTestHost.PerformanceMutationSnapshot>(TestContext.Current.CancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidOperationException($"Orchard returned no {provider} content-update measurement.");

                    using HttpResponseMessage processResponse = await http.PostAsync(
                        "/__test/indexing/process",
                        content: null,
                        TestContext.Current.CancellationToken).ConfigureAwait(false);
                    processResponse.EnsureSuccessStatusCode();
                    OrchardSearchTestHost.PerformanceProcessingSnapshot processing = await processResponse.Content
                        .ReadFromJsonAsync<OrchardSearchTestHost.PerformanceProcessingSnapshot>(TestContext.Current.CancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidOperationException("Orchard returned no indexing-task processing measurement.");

                    using HttpResponseMessage searchResponse = await http.PostAsJsonAsync(
                        $"/__test/performance/search/{provider}/{profile}",
                        new OrchardSearchTestHost.PerformanceSearchRequest(term, 0, 10),
                        TestContext.Current.CancellationToken).ConfigureAwait(false);
                    searchResponse.EnsureSuccessStatusCode();
                    OrchardSearchTestHost.PerformanceSearchSnapshot visibility = await searchResponse.Content
                        .ReadFromJsonAsync<OrchardSearchTestHost.PerformanceSearchSnapshot>(TestContext.Current.CancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidOperationException($"Orchard returned no {provider} post-update visibility measurement.");
                    bool expectedItemFound = visibility.ContentItemIds.Contains(bulk.Items[0].ContentItemId, StringComparer.Ordinal);
                    report.UpdateSamples.Add(new LatencySample(
                        provider,
                        phase,
                        iteration,
                        mutation.ElapsedMilliseconds + processing.ElapsedMilliseconds + visibility.ElapsedMilliseconds,
                        visibility.Success && expectedItemFound,
                        visibility.ContentItemIds.Length,
                        null));
                    Assert.True(visibility.Success, $"{provider} search failed after the performance update.");
                    Assert.True(expectedItemFound, $"{provider} did not expose the updated term '{term}'.");
                    sampleNumber++;
                }
            }

            report.Status = "passed";
            completed = true;
        }
        catch (Exception exception)
        {
            report.Status = "failed";
            report.Failure = $"{exception.GetType().Name}: {exception.Message}";
            throw;
        }
        finally
        {
            report.CompletedUtc = DateTimeOffset.UtcNow;
            report.SearchSummaries = Summarise(report.SearchSamples);
            report.UpdateSummaries = Summarise(report.UpdateSamples);
            try
            {
                await SaveReportAsync(report).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (app is not null)
                        await app.StopAsync().ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        if (app is not null)
                            await app.DisposeAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        if (completed && Directory.Exists(contentRoot))
                            Directory.Delete(contentRoot, recursive: true);
                        else if (Directory.Exists(contentRoot))
                            Console.WriteLine($"Orchard performance content root retained at {contentRoot}");
                    }
                }
            }
        }
    }

    private static async Task<OrchardSearchTestHost.RebuildSnapshot> BuildAsync(
        HttpClient http,
        string provider,
        string profile,
        OrchardSearchTestHost.BulkArticleResponse corpus)
    {
        using HttpResponseMessage response = await http.PostAsJsonAsync(
            $"/__test/performance/build/{provider}/{profile}",
            new OrchardSearchTestHost.PerformanceBuildRequest(corpus.Items),
            TestContext.Current.CancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<OrchardSearchTestHost.RebuildSnapshot>(TestContext.Current.CancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Orchard returned no {provider} full-build measurement.");
    }

    private static async Task AssertCursorDrainedAsync(HttpClient http, string provider, string profile)
    {
        OrchardSearchTestHost.IndexingCursorSnapshot cursor = await GetCursorAsync(http, provider, profile).ConfigureAwait(false);
        Assert.False(cursor.HasPendingContentTasks, $"Orchard left pending tasks after the {provider} performance build.");
    }

    private static async Task<OrchardSearchTestHost.IndexingCursorSnapshot> GetCursorAsync(HttpClient http, string provider, string profile)
        => await http.GetFromJsonAsync<OrchardSearchTestHost.IndexingCursorSnapshot>(
            $"/__test/indexes/{provider}/{profile}/cursor",
            TestContext.Current.CancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Orchard returned no {provider} cursor.");

    private static IEnumerable<(string Phase, int Iteration)> GetSamplePlan()
    {
        yield return ("cold", 0);
        for (int iteration = 1; iteration <= WarmupCount; iteration++)
            yield return ("warmup", iteration);
        for (int iteration = 1; iteration <= MeasuredCount; iteration++)
            yield return ("measured", iteration);
    }

    private static string GetProfileName(string provider)
        => provider.Equals("Lucene", StringComparison.Ordinal) ? "Search-Lucene" : "Search-LeanCorpus";

    private static Dictionary<string, LatencySummary> Summarise(IEnumerable<LatencySample> samples)
        => samples.GroupBy(sample => sample.Provider, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new LatencySummary(
                    group.Count(sample => sample.Phase == "cold"),
                    group.Count(sample => sample.Phase == "warmup"),
                    group.Count(sample => sample.Phase == "measured"),
                    SummariseValues(group.Where(sample => sample.Phase == "measured").Select(sample => sample.ElapsedMilliseconds))),
                StringComparer.Ordinal);

    private static LatencyStatistics SummariseValues(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        if (sorted.Length == 0)
            return new LatencyStatistics(null, null, null, null, null);
        double median = sorted.Length % 2 == 0
            ? (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2d
            : sorted[sorted.Length / 2];
        int p95Index = Math.Clamp((int)Math.Ceiling(sorted.Length * 0.95d) - 1, 0, sorted.Length - 1);
        return new LatencyStatistics(sorted[0], median, sorted[p95Index], sorted[^1], sorted.Length);
    }

    private static async Task SaveReportAsync(ProviderPerformanceReport report)
    {
        string? directory = Path.GetDirectoryName(report.OutputPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
        await File.WriteAllTextAsync(report.OutputPath, json, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<PerformanceEnvironment> CaptureEnvironmentAsync(string repositoryRoot)
    {
        string[] status = await RunCommandAsync(repositoryRoot, "git", "status", "--short").ConfigureAwait(false);
        string[] sdk = await RunCommandAsync(repositoryRoot, "dotnet", "--version").ConfigureAwait(false);
        string[] root = await RunCommandAsync(repositoryRoot, "git", "rev-parse", "--show-toplevel").ConfigureAwait(false);
        string[] branch = await RunCommandAsync(repositoryRoot, "git", "branch", "--show-current").ConfigureAwait(false);
        string[] commit = await RunCommandAsync(repositoryRoot, "git", "rev-parse", "HEAD").ConfigureAwait(false);
        return new PerformanceEnvironment(
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            AppContext.TargetFrameworkName ?? "unknown",
            sdk.FirstOrDefault() ?? "unknown",
            GetCpuModel(),
            Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            GC.GetTotalMemory(forceFullCollection: false),
            Stopwatch.Frequency,
            System.Runtime.GCSettings.IsServerGC,
            root.FirstOrDefault() ?? repositoryRoot,
            branch.FirstOrDefault() ?? "unknown",
            commit.FirstOrDefault() ?? "unknown",
            status.Length > 0,
            status);
    }

    private static async Task<string[]> RunCommandAsync(string workingDirectory, string executable, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (string argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start())
            return [];
        string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode == 0
            ? output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
    }

    private static string GetCpuModel()
    {
        if (File.Exists("/proc/cpuinfo"))
        {
            string? line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(value => value.StartsWith("model name", StringComparison.Ordinal));
            if (line is not null)
            {
                int separator = line.IndexOf(':');
                if (separator >= 0)
                    return line[(separator + 1)..].Trim();
            }
        }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown";
    }

    private static string FindRepositoryRoot()
    {
        foreach (string startingPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? current = new(Path.GetFullPath(startingPath));
            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
                    return current.FullName;
                current = current.Parent;
            }
        }
        throw new DirectoryNotFoundException("Could not locate the LeanCorpus Git repository root for performance provenance.");
    }

    private sealed class ProviderPerformanceReport
    {
        public string Status { get; set; } = "running";
        public string OutputPath { get; set; } = string.Empty;
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset CompletedUtc { get; set; }
        public int TargetDocumentCount { get; set; }
        public string[] MeasurementNotes { get; set; } = [];
        public PerformanceEnvironment? Environment { get; set; }
        public List<InitialBuildRun> InitialBuilds { get; } = [];
        public List<RebuildRun> Rebuilds { get; } = [];
        public List<LatencySample> SearchSamples { get; } = [];
        public List<LatencySample> UpdateSamples { get; } = [];
        public Dictionary<string, LatencySummary> SearchSummaries { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, LatencySummary> UpdateSummaries { get; set; } = new(StringComparer.Ordinal);
        public string? Failure { get; set; }
    }

    private sealed record PerformanceEnvironment(
        string OsDescription,
        string OsArchitecture,
        string ProcessArchitecture,
        string Runtime,
        string TargetFramework,
        string SdkVersion,
        string CpuModel,
        int ProcessorCount,
        long AvailableMemoryBytes,
        long ManagedHeapBytesAtCapture,
        long StopwatchFrequency,
        bool ServerGc,
        string RepositoryRoot,
        string Branch,
        string Commit,
        bool Dirty,
        string[] GitStatus);

    private sealed record RebuildRun(string Provider, int Iteration, double ElapsedMilliseconds, long LastTaskId);

    private sealed record InitialBuildRun(string Provider, double ElapsedMilliseconds, long LastTaskId);

    private sealed record LatencySample(
        string Provider,
        string Phase,
        int Iteration,
        double ElapsedMilliseconds,
        bool Success,
        int ResultCount,
        long? ReportedTotalCount);

    private sealed record LatencySummary(int ColdSamples, int WarmupSamples, int MeasuredSamples, LatencyStatistics Measured);

    private sealed record LatencyStatistics(double? MinMilliseconds, double? MedianMilliseconds, double? P95Milliseconds, double? MaxMilliseconds, int? SampleCount);
}
