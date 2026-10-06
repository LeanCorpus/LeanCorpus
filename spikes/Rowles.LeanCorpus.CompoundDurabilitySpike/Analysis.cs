using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rowles.LeanCorpus.CompoundDurabilitySpike;

internal static partial class SpikeRunner
{
    private static readonly string[] PrimaryPlatforms = ["linux-ext4", "windows-ntfs"];
    private static readonly string[] Representations = ["loose", "compound"];
    private static readonly string[] Lifecycles = ["fresh", "reopened-first", "reopened-steady"];
    private static readonly bool[] DurabilityLevels = [false, true];
    private static readonly string[] PairedMetricNames =
    [
        "durability_saving_ms",
        "operation_saving_ms",
        "loose_durability_penalty_ms",
        "compound_durability_penalty_ms",
        "durability_penalty_difference_in_differences_ms",
        "durability_candidate_file_saving",
        "durability_candidate_byte_saving",
        "file_persist_request_saving",
        "compound_pack_ms"
    ];

    public static Task<int> AnalyseAsync(string root, Arguments arguments)
    {
        string aggregateRoot = Path.GetFullPath(root);
        PlatformData[] platforms = PrimaryPlatforms.Select(platform => LoadPlatform(aggregateRoot, platform)).ToArray();
        ValidateCrossPlatformIdentity(platforms, aggregateRoot);
        foreach (PlatformData platform in platforms)
        {
            WritePairedAnalysis(platform);
            WriteCardinalitySummary(platform);
        }

        int[] allLaunches = [1, 2, 3, 4, 5];
        string coreClassification = ClassifyCore(platforms, allLaunches);
        int[][] sensitivityLaunchSets = allLaunches
            .Select(omittedLaunch => allLaunches.Where(launch => launch != omittedLaunch).ToArray())
            .ToArray();
        var sensitivity = new SortedDictionary<int, string>();
        for (int index = 0; index < allLaunches.Length; index++)
            sensitivity[allLaunches[index]] = ClassifyCore(platforms, sensitivityLaunchSets[index]);

        int[][] stabilityLaunchSets = [allLaunches, .. sensitivityLaunchSets];
        Dictionary<string, string> stableSupportingLifecycle = platforms.ToDictionary(
            static platform => platform.PlatformId,
            platform => FindStableSupportingLifecycle(platform, stabilityLaunchSets) ?? "none",
            StringComparer.Ordinal);
        bool classificationChanged = sensitivity.Values.Any(value => !string.Equals(value, coreClassification, StringComparison.Ordinal));
        bool sameLifecycleUnstable = coreClassification == "premise_supported"
            && stableSupportingLifecycle.Values.Any(static lifecycle => lifecycle == "none");
        bool otherClassificationUnstable = coreClassification switch
        {
            "platform_specific" => !PlatformSpecificStableAcrossLaunchSets(platforms, allLaunches, stabilityLaunchSets),
            "mechanism_present_but_packing_cancels" => !MechanismStableAcrossLaunchSets(platforms, stabilityLaunchSets),
            _ => false
        };
        bool sensitivityChanged = classificationChanged || sameLifecycleUnstable || otherClassificationUnstable;
        string classification = sensitivityChanged ? "inconclusive" : coreClassification;
        string rationale = BuildRationale(classification, coreClassification, sensitivityChanged, platforms);
        string summaryMarkdown = BuildSummaryMarkdown(aggregateRoot, platforms, classification, coreClassification, rationale,
            sensitivity, stableSupportingLifecycle);
        object summaryJson = BuildSummaryJson(aggregateRoot, platforms, classification, coreClassification, rationale,
            sensitivity, stableSupportingLifecycle);

        File.WriteAllText(Path.Combine(aggregateRoot, "summary.md"), summaryMarkdown, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(aggregateRoot, "summary.json"),
            JsonSerializer.Serialize(summaryJson, new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false));
        foreach (PlatformData platform in platforms)
        {
            File.WriteAllText(Path.Combine(platform.Root, "summary.md"), summaryMarkdown, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(platform.Root, "summary.json"),
                JsonSerializer.Serialize(summaryJson, new JsonSerializerOptions { WriteIndented = true }) + "\n",
                new UTF8Encoding(false));
            WriteSha256Manifest(platform.Root);
        }
        WriteSha256Manifest(aggregateRoot);
        Console.WriteLine($"classification={classification} pre_sensitivity_classification={coreClassification} sensitivity_changed={sensitivityChanged.ToString().ToLowerInvariant()}");
        return Task.FromResult(classification == "inconclusive" ? 2 : 0);
    }

    private static PlatformData LoadPlatform(string aggregateRoot, string platformId)
    {
        string platformRoot = Path.Combine(aggregateRoot, platformId);
        return new PlatformData(
            platformId,
            platformRoot,
            ReadJsonIfExists(Path.Combine(platformRoot, "environment.json")),
            ReadJsonIfExists(Path.Combine(platformRoot, "source-and-assembly-hashes.json")),
            ReadJsonIfExists(Path.Combine(platformRoot, "dataset-identity.json")),
            CsvIfExists(Path.Combine(platformRoot, "production-trials.csv")),
            CsvIfExists(Path.Combine(platformRoot, "production-pairs.csv")),
            CsvIfExists(Path.Combine(platformRoot, "cardinality-trials.csv")),
            CsvIfExists(Path.Combine(platformRoot, "baseline-topology-loose.csv")),
            CsvIfExists(Path.Combine(platformRoot, "baseline-topology-compound.csv")),
            CsvIfExists(Path.Combine(platformRoot, "execution-order.csv")),
            CsvIfExists(Path.Combine(platformRoot, "cardinality-order.csv")),
            CsvIfExists(Path.Combine(platformRoot, "cardinality-partitions.csv")));
    }

    private static Dictionary<string, string>? ReadJsonIfExists(string path)
    {
        if (!File.Exists(path))
            return null;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText(),
            StringComparer.Ordinal);
    }

    private static List<Dictionary<string, string>> CsvIfExists(string path)
        => File.Exists(path) ? Csv.Read(path) : [];

    private static void ValidateCrossPlatformIdentity(IReadOnlyList<PlatformData> platforms, string aggregateRoot)
    {
        string expectedExperiment = Path.GetFileName(Path.TrimEndingDirectorySeparator(aggregateRoot));
        foreach (PlatformData platform in platforms)
            platform.Validate(expectedExperiment);
        string[] hashes = platforms.Select(static platform => platform.CorpusSha256)
            .Where(static hash => hash.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (hashes.Length == 1)
        {
            var identity = new
            {
                experiment_sha = expectedExperiment,
                canonical_content_sha256 = hashes[0],
                platforms = platforms.Select(platform => new
                {
                    platform_id = platform.PlatformId,
                    canonical_content_sha256 = platform.CorpusSha256,
                    data_forge_version = platform.DatasetValue("dataForgeVersion"),
                    profile_id = platform.DatasetValue("profileId"),
                    profile_version = platform.DatasetValue("profileVersion"),
                    seed = platform.GenerationValue("seed"),
                    record_count = platform.GenerationValue("record_count")
                })
            };
            File.WriteAllText(Path.Combine(aggregateRoot, "dataset-identity-cross-platform.json"),
                JsonSerializer.Serialize(identity, new JsonSerializerOptions { WriteIndented = true }) + "\n",
                new UTF8Encoding(false));
        }
        else
        {
            foreach (PlatformData platform in platforms)
                platform.ValidationErrors.Add("The primary platforms do not have the same canonical DataForge content SHA-256.");
        }
    }

    private static void WritePairedAnalysis(PlatformData platform)
    {
        string path = Path.Combine(platform.Root, "paired-analysis.csv");
        Csv.Create(path, CsvSchemas.PairedAnalysis);
        foreach (string lifecycle in Lifecycles)
        foreach (string metric in PairedMetricNames)
        {
            MetricPoint[] points = BuildLifecycleMetrics(platform, lifecycle)[metric].ToArray();
            AppendAnalysisStats(path, platform.PlatformId, lifecycle, metric, points, "all", [1, 2, 3, 4, 5]);
            foreach (int omitted in new[] { 1, 2, 3, 4, 5 })
                AppendAnalysisStats(path, platform.PlatformId, lifecycle, metric, points, $"omit-{omitted}",
                    new[] { 1, 2, 3, 4, 5 }.Where(launch => launch != omitted).ToArray());
        }
    }

    private static void AppendAnalysisStats(
        string path,
        string platform,
        string lifecycle,
        string metric,
        IReadOnlyList<MetricPoint> points,
        string launchSet,
        IReadOnlyList<int> launches)
    {
        MetricSummary summary = Summarise(points, launches, BootstrapSeed(platform, lifecycle, metric, launchSet));
        Csv.Append(path, CsvSchemas.PairedAnalysis,
        [
            platform, lifecycle, launchSet, metric, summary.N, summary.Median, summary.Q1, summary.Q3, summary.Iqr,
            summary.CiLow, summary.CiHigh, summary.Minimum, summary.Maximum,
            JsonSerializer.Serialize(summary.LaunchMedians)
        ]);
    }

    private static void WriteCardinalitySummary(PlatformData platform)
    {
        string path = Path.Combine(platform.Root, "cardinality-summary.csv");
        string[] columns =
        [
            "payload_bytes", "partition_mode", "object_count", "valid_n", "median_durability_sync_ms", "q1_ms", "q3_ms",
            "bootstrap_95ci_low_ms", "bootstrap_95ci_high_ms", "median_file_persist_requests", "median_file_persist_elapsed_ms",
            "spearman_rho", "median_non_decreasing"
        ];
        Csv.Create(path, columns);
        foreach (int payloadBytes in Dataset.CardinalityPayloadSizes)
        foreach (string mode in CardinalityModes)
        {
            var groups = new List<(int Count, Dictionary<string, string>[] Rows, MetricSummary Time, MetricSummary Requests, MetricSummary FileTime)>();
            foreach (int count in CardinalityCounts)
            {
                Dictionary<string, string>[] rows = ValidCardinalityRows(platform, [1, 2, 3])
                    .Where(row => ParseInt(row["payload_bytes"]) == payloadBytes
                        && row["partition_mode"] == mode
                        && ParseInt(row["object_count"]) == count).ToArray();
                MetricSummary time = Summarise(rows.Select(row => new MetricPoint(ParseInt(row["launch"]),
                    ParseDouble(row["durability_sync_ms"]) ?? double.NaN)), [1, 2, 3],
                    BootstrapSeed(platform.PlatformId, payloadBytes.ToString(CultureInfo.InvariantCulture), mode, count.ToString(CultureInfo.InvariantCulture)));
                MetricSummary requests = Summarise(rows.Select(row => new MetricPoint(ParseInt(row["launch"]),
                    ParseDouble(row["file_persist_requests"]) ?? double.NaN)), [1, 2, 3], 401 + count);
                MetricSummary fileTime = Summarise(rows.Select(row => new MetricPoint(ParseInt(row["launch"]),
                    ParseDouble(row["file_persist_elapsed_ms"]) ?? double.NaN)), [1, 2, 3], 801 + count);
                groups.Add((count, rows, time, requests, fileTime));
            }
            double[] medians = groups.Where(static group => group.Time.Median.HasValue)
                .Select(static group => group.Time.Median!.Value).ToArray();
            double rho = Spearman(CardinalityCounts.Select(static count => (double)count).ToArray(), medians);
            bool nonDecreasing = medians.Length == CardinalityCounts.Length
                && medians.Zip(medians.Skip(1)).All(static pair => pair.Second >= pair.First);
            foreach (var group in groups)
                Csv.Append(path, columns,
                [
                    payloadBytes, mode, group.Count, group.Time.N, group.Time.Median, group.Time.Q1, group.Time.Q3,
                    group.Time.CiLow, group.Time.CiHigh, group.Requests.Median, group.FileTime.Median, rho, nonDecreasing
                ]);
        }
        platform.CardinalitySummaryRows = Csv.Read(path);
    }

    private static Dictionary<string, List<MetricPoint>> BuildLifecycleMetrics(PlatformData platform, string lifecycle)
    {
        var result = PairedMetricNames.ToDictionary(static name => name, static _ => new List<MetricPoint>(), StringComparer.Ordinal);
        var production = platform.Production.Where(row => !ParseBoolean(row["warmup"]))
            .ToDictionary(row => (
                Launch: ParseInt(row["launch"]),
                Observation: ParseInt(row["observation"]),
                Lifecycle: row["lifecycle"],
                Representation: row["representation"],
                Durable: ParseBoolean(row["durable"])));
        var validPairKeys = platform.ProductionPairs.Where(static row => row["pair_status"] == "valid")
            .Select(row => (Launch: ParseInt(row["launch"]), Observation: ParseInt(row["observation"]),
                Lifecycle: row["lifecycle"], Durable: ParseBoolean(row["durable"])))
            .ToHashSet();

        foreach (int launch in new[] { 1, 2, 3, 4, 5 })
        for (int observation = 1; observation <= 5; observation++)
        {
            bool TryPair(bool durable, out Dictionary<string, string> loose, out Dictionary<string, string> compound)
            {
                bool hasLoose = production.TryGetValue((launch, observation, lifecycle, "loose", durable), out loose!);
                bool hasCompound = production.TryGetValue((launch, observation, lifecycle, "compound", durable), out compound!);
                return hasLoose && hasCompound && validPairKeys.Contains((launch, observation, lifecycle, durable));
            }

            if (!TryPair(true, out Dictionary<string, string> looseDurable, out Dictionary<string, string> compoundDurable))
                continue;
            Add(result, "durability_saving_ms", launch, ParseDoubleDifference(
                ParseDouble(looseDurable["durability_sync_ms"]), ParseDouble(compoundDurable["durability_sync_ms"])));
            Add(result, "operation_saving_ms", launch, ParseDoubleDifference(
                ParseDouble(looseDurable["operation_ms"]), ParseDouble(compoundDurable["operation_ms"])));
            Add(result, "durability_candidate_file_saving", launch, ParseDoubleDifference(
                ParseDouble(looseDurable["durability_candidate_files"]), ParseDouble(compoundDurable["durability_candidate_files"])));
            Add(result, "durability_candidate_byte_saving", launch, ParseDoubleDifference(
                ParseDouble(looseDurable["durability_candidate_bytes"]), ParseDouble(compoundDurable["durability_candidate_bytes"])));
            Add(result, "file_persist_request_saving", launch, ParseDoubleDifference(
                ParseDouble(looseDurable["file_persist_requests"]), ParseDouble(compoundDurable["file_persist_requests"])));
            Add(result, "compound_pack_ms", launch, ParseDouble(compoundDurable["compound_pack_ms"]));
            if (!TryPair(false, out Dictionary<string, string> looseNonDurable, out Dictionary<string, string> compoundNonDurable))
                continue;

            double? loosePenalty = ParseDoubleDifference(ParseDouble(looseDurable["commit_call_ms"]), ParseDouble(looseNonDurable["commit_call_ms"]));
            double? compoundPenalty = ParseDoubleDifference(ParseDouble(compoundDurable["commit_call_ms"]), ParseDouble(compoundNonDurable["commit_call_ms"]));
            Add(result, "loose_durability_penalty_ms", launch, loosePenalty);
            Add(result, "compound_durability_penalty_ms", launch, compoundPenalty);
            Add(result, "durability_penalty_difference_in_differences_ms", launch,
                ParseDoubleDifference(loosePenalty, compoundPenalty));
        }
        return result;
    }

    private static void Add(Dictionary<string, List<MetricPoint>> metrics, string name, int launch, double? value)
    {
        if (value is double finite && double.IsFinite(finite))
            metrics[name].Add(new MetricPoint(launch, finite));
    }

    private static string ClassifyCore(IReadOnlyList<PlatformData> platforms, IReadOnlyList<int> launches)
    {
        if (platforms.Count != 2 || platforms.Any(platform => !platform.DataReady(launches)))
            return "inconclusive";
        if (platforms.Any(static platform => platform.CorrectnessFailure || !platform.SourceSemanticsValid))
            return "inconclusive";
        if (platforms.Select(static platform => platform.CorpusSha256)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            return "inconclusive";

        bool linuxSupported = PlatformSupports(platforms.Single(static platform => platform.PlatformId == "linux-ext4"), launches);
        bool windowsSupported = PlatformSupports(platforms.Single(static platform => platform.PlatformId == "windows-ntfs"), launches);
        if (linuxSupported && windowsSupported)
            return "premise_supported";
        if (linuxSupported != windowsSupported)
        {
            PlatformData other = platforms.Single(platform =>
                platform.PlatformId == (linuxSupported ? "windows-ntfs" : "linux-ext4"));
            return StableNeutralOrContrary(other, launches) ? "platform_specific" : "inconclusive";
        }
        if (platforms.Any(platform => MechanismPresentButPackingCancels(platform, launches)))
            return "mechanism_present_but_packing_cancels";
        bool anyDirectSaving = platforms.Any(platform => HasPositiveDurabilitySaving(platform, launches));
        if (!anyDirectSaving && !CardinalitySupportsMechanism(platforms, [1, 2, 3], requireMonotonic: true))
            return "premise_rejected";
        return "inconclusive";
    }

    private static bool PlatformSupports(PlatformData platform, IReadOnlyList<int> launches)
    {
        if (!HasStructuralReduction(platform, launches))
            return false;
        foreach (string lifecycle in Lifecycles)
        {
            Dictionary<string, List<MetricPoint>> metrics = BuildLifecycleMetrics(platform, lifecycle);
            if (Summarise(metrics["durability_saving_ms"], launches, 1901).Median is not > 0
                || Summarise(metrics["operation_saving_ms"], launches, 1902).Median is not > 0)
                continue;
            return true;
        }
        return false;
    }

    private static string? FindStableSupportingLifecycle(PlatformData platform, IReadOnlyList<int[]> launchSets)
    {
        foreach (string lifecycle in Lifecycles)
        {
            Dictionary<string, List<MetricPoint>> metrics = BuildLifecycleMetrics(platform, lifecycle);
            if (launchSets.All(launches =>
                    Summarise(metrics["durability_saving_ms"], launches, 1901).Median is > 0
                    && Summarise(metrics["operation_saving_ms"], launches, 1902).Median is > 0))
                return lifecycle;
        }

        return null;
    }

    private static bool HasStructuralReduction(PlatformData platform, IReadOnlyList<int> launches)
    {
        MetricPoint[] fileSaving = Lifecycles.SelectMany(lifecycle => BuildLifecycleMetrics(platform, lifecycle)["durability_candidate_file_saving"])
            .Where(point => launches.Contains(point.Launch)).ToArray();
        MetricPoint[] requestSaving = Lifecycles.SelectMany(lifecycle => BuildLifecycleMetrics(platform, lifecycle)["file_persist_request_saving"])
            .Where(point => launches.Contains(point.Launch)).ToArray();
        return Summarise(fileSaving, launches, 2101).Median is > 0
            && Summarise(requestSaving, launches, 2102).Median is > 0;
    }

    private static bool StableNeutralOrContrary(PlatformData platform, IReadOnlyList<int> launches)
    {
        foreach (string lifecycle in Lifecycles)
        foreach (string metric in new[] { "durability_saving_ms", "operation_saving_ms" })
        {
            List<MetricPoint> points = BuildLifecycleMetrics(platform, lifecycle)[metric];
            if (Summarise(points, launches, 2201).Median is null)
                return false;
        }
        return true;
    }

    private static bool MechanismPresentButPackingCancels(PlatformData platform, IReadOnlyList<int> launches)
    {
        if (!HasStructuralReduction(platform, launches))
            return false;
        foreach (string lifecycle in Lifecycles)
        {
            Dictionary<string, List<MetricPoint>> metrics = BuildLifecycleMetrics(platform, lifecycle);
            if (Summarise(metrics["durability_saving_ms"], launches, 2301).Median is not > 0
                || Summarise(metrics["operation_saving_ms"], launches, 2302).Median is > 0
                || Summarise(metrics["compound_pack_ms"], launches, 2303).Median is not > 0)
                return false;
        }
        return true;
    }

    private static bool PlatformSpecificStableAcrossLaunchSets(
        IReadOnlyList<PlatformData> platforms,
        IReadOnlyList<int> allLaunches,
        IReadOnlyList<int[]> launchSets)
    {
        bool linuxSupported = PlatformSupports(platforms.Single(static platform => platform.PlatformId == "linux-ext4"), allLaunches);
        PlatformData other = platforms.Single(platform =>
            platform.PlatformId == (linuxSupported ? "windows-ntfs" : "linux-ext4"));
        return StableNeutralOrContraryAcrossLaunchSets(other, launchSets);
    }

    private static bool StableNeutralOrContraryAcrossLaunchSets(
        PlatformData platform,
        IReadOnlyList<int[]> launchSets)
    {
        foreach (string lifecycle in Lifecycles)
        foreach (string metric in new[] { "durability_saving_ms", "operation_saving_ms" })
        {
            List<MetricPoint> points = BuildLifecycleMetrics(platform, lifecycle)[metric];
            int? fullSign = null;
            foreach (int[] launches in launchSets)
            {
                double? median = Summarise(points, launches, 2201).Median;
                if (median is null)
                    return false;
                int sign = Sign(median);
                if (fullSign is int expectedSign && expectedSign != sign)
                    return false;
                fullSign = sign;
            }
        }
        return true;
    }

    private static bool MechanismStableAcrossLaunchSets(
        IReadOnlyList<PlatformData> platforms,
        IReadOnlyList<int[]> launchSets)
        => platforms.Any(platform => launchSets.All(launches => MechanismPresentButPackingCancels(platform, launches)));

    private static bool HasPositiveDurabilitySaving(PlatformData platform, IReadOnlyList<int> launches)
        => Lifecycles.Any(lifecycle =>
            Summarise(BuildLifecycleMetrics(platform, lifecycle)["durability_saving_ms"], launches, 2401).Median is > 0);

    private static bool CardinalitySupportsMechanism(
        IReadOnlyList<PlatformData> platforms,
        IReadOnlyList<int> launches,
        bool requireMonotonic)
    {
        foreach (PlatformData platform in platforms)
        foreach (int payloadBytes in Dataset.CardinalityPayloadSizes)
        foreach (string mode in CardinalityModes)
        {
            double? previous = null;
            var medians = new List<double>();
            foreach (int count in CardinalityCounts)
            {
                Dictionary<string, string>[] rows = ValidCardinalityRows(platform, launches)
                    .Where(row => ParseInt(row["payload_bytes"]) == payloadBytes && row["partition_mode"] == mode
                        && ParseInt(row["object_count"]) == count).ToArray();
                double? median = Summarise(rows.Select(row => new MetricPoint(ParseInt(row["launch"]),
                    ParseDouble(row["durability_sync_ms"]) ?? double.NaN)), launches, 2500 + count).Median;
                if (median is null)
                    return false;
                if (requireMonotonic && previous is not null && median < previous)
                    return false;
                previous = median;
                medians.Add(median.Value);
            }
            if (Spearman(CardinalityCounts.Select(static count => (double)count).ToArray(), medians) <= 0)
                return false;
        }
        return true;
    }

    private static Dictionary<string, string>[] ValidCardinalityRows(PlatformData platform, IReadOnlyList<int> launches)
        => platform.Cardinality.Where(row => launches.Contains(ParseInt(row["launch"]))
            && !ParseBoolean(row["warmup"])
            && row["payload_reconstruction_pass"] == "true" && string.IsNullOrEmpty(row["error"])
            && ParseDouble(row["durability_sync_ms"]) is not null).ToArray();

    private static MetricSummary Summarise(IEnumerable<MetricPoint> source, IReadOnlyList<int> launches, int seed)
    {
        MetricPoint[] points = source.Where(point => launches.Contains(point.Launch) && double.IsFinite(point.Value)).ToArray();
        double[] sorted = points.Select(static point => point.Value).Order().ToArray();
        if (sorted.Length == 0)
            return MetricSummary.Empty;
        double median = Quantile(sorted, 0.5);
        var launchMedians = points.GroupBy(static point => point.Launch).OrderBy(static group => group.Key)
            .ToDictionary(static group => group.Key.ToString(CultureInfo.InvariantCulture),
                static group => (double?)Quantile(group.Select(static point => point.Value).Order().ToArray(), 0.5));
        double ciLow = median;
        double ciHigh = median;
        if (sorted.Length > 1 && launches.Count > 1)
        {
            var byLaunch = points.GroupBy(static point => point.Launch)
                .ToDictionary(static group => group.Key, static group => group.Select(static point => point.Value).ToArray());
            int[] available = launches.Where(byLaunch.ContainsKey).ToArray();
            if (available.Length > 1)
            {
                double[] bootstrap = new double[10_000];
                var random = new Random(seed);
                for (int iteration = 0; iteration < bootstrap.Length; iteration++)
                {
                    var sample = new List<double>(points.Length);
                    for (int cluster = 0; cluster < available.Length; cluster++)
                        sample.AddRange(byLaunch[available[random.Next(available.Length)]]);
                    sample.Sort();
                    bootstrap[iteration] = Quantile(sample, 0.5);
                }
                Array.Sort(bootstrap);
                ciLow = Quantile(bootstrap, 0.025);
                ciHigh = Quantile(bootstrap, 0.975);
            }
        }
        double q1 = Quantile(sorted, 0.25);
        double q3 = Quantile(sorted, 0.75);
        return new MetricSummary(sorted.Length, median, q1, q3, q3 - q1, ciLow, ciHigh,
            sorted[0], sorted[^1], launchMedians);
    }

    private static int BootstrapSeed(params string[] inputs)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (byte value in Encoding.UTF8.GetBytes(string.Join("|", inputs)))
            {
                hash ^= value;
                hash *= 16777619;
            }
            return (int)(hash & 0x7fffffff);
        }
    }

    private static int Sign(double? value) => value is > 0 ? 1 : value is < 0 ? -1 : 0;

    private static double Spearman(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        if (x.Count != y.Count || x.Count < 2)
            return double.NaN;
        return Pearson(Rank(x), Rank(y));
    }

    private static double[] Rank(IReadOnlyList<double> values)
    {
        int[] order = Enumerable.Range(0, values.Count).OrderBy(index => values[index]).ToArray();
        double[] ranks = new double[values.Count];
        int cursor = 0;
        while (cursor < order.Length)
        {
            int end = cursor + 1;
            while (end < order.Length && values[order[end]].Equals(values[order[cursor]]))
                end++;
            double averageRank = (cursor + 1 + end) / 2d;
            for (int index = cursor; index < end; index++)
                ranks[order[index]] = averageRank;
            cursor = end;
        }
        return ranks;
    }

    private static double Pearson(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        double meanX = x.Average();
        double meanY = y.Average();
        double numerator = 0;
        double varianceX = 0;
        double varianceY = 0;
        for (int index = 0; index < x.Count; index++)
        {
            double dx = x[index] - meanX;
            double dy = y[index] - meanY;
            numerator += dx * dy;
            varianceX += dx * dx;
            varianceY += dy * dy;
        }
        return varianceX == 0 || varianceY == 0 ? 0 : numerator / Math.Sqrt(varianceX * varianceY);
    }

    private static double Quantile(IReadOnlyList<double> sorted, double probability)
    {
        if (sorted.Count == 1)
            return sorted[0];
        double position = (sorted.Count - 1) * probability;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        return lower == upper ? sorted[lower]
            : sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static string BuildRationale(
        string classification,
        string baseClassification,
        bool sensitivityChanged,
        IReadOnlyList<PlatformData> platforms)
    {
        if (sensitivityChanged)
            return $"The all-five-launch classification was {baseClassification}, but a predeclared leave-one-launch-out or same-lifecycle stability check failed; the result is inconclusive.";
        if (classification == "premise_supported")
            return "Both primary platforms have exact matched topology, lower compound persistence-object counts and requests, positive paired durability savings, and positive paired end-to-end savings in at least one lifecycle that remain positive under every launch omission.";
        if (classification == "platform_specific")
            return "Exactly one primary platform meets the supported criteria, while the other has complete and leave-one-launch-stable neutral or contrary evidence.";
        if (classification == "mechanism_present_but_packing_cancels")
            return "Compound consistently reduces directly measured durability-protocol cost, while measured operation time does not improve and compound construction work is present.";
        if (classification == "premise_rejected")
            return "Neither platform has a positive median paired durability saving in any declared lifecycle, and the compact cardinality confirmation does not support a positive monotonic relationship across both payload sizes and partition shapes.";
        string[] reasons = platforms.SelectMany(static platform => platform.ValidationErrors).Distinct(StringComparer.Ordinal).ToArray();
        return reasons.Length == 0
            ? "The evidence does not meet another predeclared disposition or is unstable under leave-one-launch-out analysis."
            : "Required provenance, topology, completeness, or correctness checks failed: " + string.Join("; ", reasons);
    }

    private static string BuildSummaryMarkdown(
        string aggregateRoot,
        IReadOnlyList<PlatformData> platforms,
        string classification,
        string baseClassification,
        string rationale,
        IReadOnlyDictionary<int, string> sensitivity,
        IReadOnlyDictionary<string, string> stableSupportingLifecycle)
    {
        var output = new StringBuilder();
        output.AppendLine("# Spike 1B: Compound packing and durability mechanism");
        output.AppendLine();
        output.AppendLine($"Experiment commit: {ExpectedExperiment(platforms)}");
        output.AppendLine($"Evidence root: {Path.GetFullPath(aggregateRoot)}");
        output.AppendLine($"Classification: **{classification}**");
        output.AppendLine();
        output.AppendLine("Spike 1A remains separate exploratory evidence. Its findings and evidence pack were not rerun or changed by this confirmation run.");
        output.AppendLine();
        output.AppendLine("This replacement is the publication-preferred Spike 1B confirmation and supersedes `06083eecc7492d6c5671c47ba5704eb807c63fd2` for publication because the observer-induced candidate metadata pass and reopened-steady writer-open control were corrected. The earlier evidence remains preserved as historical evidence; it is not treated as invalid or fabricated.");
        output.AppendLine();
        output.AppendLine("## Provenance and dataset identity");
        output.AppendLine();
        output.AppendLine("| Platform | Clean commit | Spike assembly SHA-256 | Core assembly SHA-256 | OS / runtime / SDK | Filesystem | Canonical dataset SHA-256 | Status |");
        output.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (PlatformData platform in platforms)
            output.AppendLine($"| {platform.PlatformId} | {platform.ExperimentSha} | {platform.SpikeAssemblySha} | {platform.CoreAssemblySha} | {platform.EnvironmentValue("operatingSystem")} / {platform.EnvironmentValue("runtime")} / {platform.EnvironmentValue("sdk")} | {platform.EnvironmentValue("fileSystem")} | {platform.CorpusSha256} | {(platform.DataReady([1, 2, 3, 4, 5]) ? "complete" : "incomplete")} |");
        output.AppendLine();
        PlatformData? first = platforms.FirstOrDefault();
        output.AppendLine($"Dataset profile: {first?.DatasetValue("profileId") ?? "missing"} v{first?.DatasetValue("profileVersion") ?? "missing"}; seed {first?.GenerationValue("seed") ?? "missing"}; records {first?.GenerationValue("record_count") ?? "missing"}.");
        output.AppendLine("Records were generated independently on each platform by the checked-out DataForge materialiser. The canonical-record SHA-256 was compared before measurements.");
        output.AppendLine();
        output.AppendLine("## Baseline topology comparison");
        output.AppendLine();
        output.AppendLine("| Platform | Loose baseline vector | Compound baseline vector | Exact topology match |");
        output.AppendLine("|---|---|---|---|");
        foreach (PlatformData platform in platforms)
            output.AppendLine($"| {platform.PlatformId} | {platform.TopologyVector("loose")} | {platform.TopologyVector("compound")} | {platform.BaselineTopologyPass} |");
        output.AppendLine();
        output.AppendLine("Each manifest was extracted from committed segment metadata and stored document IDs, with one row per segment and physical file counts and bytes.");
        output.AppendLine();
        AppendProductionTiming(output, platforms);
        AppendPersistenceSummary(output, platforms);
        AppendPairedSummary(output, platforms);
        AppendLaunchSensitivity(output, platforms, sensitivity);
        AppendSameLifecycleStability(output, stableSupportingLifecycle);
        AppendCardinalitySummary(output, platforms);
        AppendCorrectnessSummary(output, platforms);
        output.AppendLine("## Classification");
        output.AppendLine();
        output.AppendLine($"All-five-launch classification before sensitivity: {baseClassification}.");
        output.AppendLine($"Final classification after applying the five leave-one-launch-out checks: {classification}.");
        output.AppendLine($"Compared with the previous `premise_supported` result, the replacement classification {(classification == "premise_supported" ? "agrees" : "differs")}.");
        output.AppendLine();
        output.AppendLine(rationale);
        output.AppendLine();
        output.AppendLine("A positive median is the predeclared direction for savings; no additional effect-size threshold was introduced. Paired confidence intervals use a launch-cluster bootstrap with 10,000 resamples.");
        output.AppendLine();
        output.AppendLine("The reopened-steady priming commit is durable and unmeasured. Its generation, durable writer-open setting, and inherited-file persistence request floor are verified before the measured batch. The measured durability setting is applied after priming.");
        output.AppendLine();
        output.AppendLine("The production compound temporary output remained non-durable. The real pack, close, dirty registration, rename, loose-member deletion, and later durable commit sequence was observed without an added .cfs.tmp persistence request.");
        output.AppendLine();
        output.AppendLine("Spike 2 was not started.");
        return output.ToString();
    }

    private static void AppendProductionTiming(StringBuilder output, IReadOnlyList<PlatformData> platforms)
    {
        string[] metrics =
        [
            "index_ms", "forced_flush_ms", "compound_pack_ms", "metadata_prepare_ms", "durability_sync_ms",
            "post_commit_ms", "commit_call_ms", "operation_ms"
        ];
        output.AppendLine("## Production timing by lifecycle, representation, and durability");
        output.AppendLine();
        output.AppendLine("Warm-ups are retained in raw data but excluded from summaries. Failed observations remain in the raw CSV; only valid observations enter paired analysis.");
        output.AppendLine();
        output.AppendLine("| Platform | Lifecycle | Representation | Durable | n | Metric | Median ms | Q1 ms | Q3 ms | IQR ms | 95% bootstrap CI |");
        output.AppendLine("|---|---|---|---:|---:|---|---:|---:|---:|---:|---|");
        foreach (PlatformData platform in platforms)
        foreach (string lifecycle in Lifecycles)
        foreach (string representation in Representations)
        foreach (bool durable in DurabilityLevels)
        foreach (string metric in metrics)
        {
            Dictionary<string, string>[] rows = ValidProductionRows(platform)
                .Where(row => row["lifecycle"] == lifecycle && row["representation"] == representation
                    && ParseBoolean(row["durable"]) == durable).ToArray();
            MetricSummary stats = Summarise(rows.Select(row => new MetricPoint(ParseInt(row["launch"]),
                ParseDouble(row[metric]) ?? double.NaN)), [1, 2, 3, 4, 5],
                BootstrapSeed(platform.PlatformId, lifecycle, representation, durable.ToString(), metric));
            output.AppendLine($"| {platform.PlatformId} | {lifecycle} | {representation} | {durable} | {stats.N} | {metric} | {F(stats.Median)} | {F(stats.Q1)} | {F(stats.Q3)} | {F(stats.Iqr)} | {stats.CiText} |");
        }
        output.AppendLine();
    }

    private static void AppendPersistenceSummary(StringBuilder output, IReadOnlyList<PlatformData> platforms)
    {
        output.AppendLine("## Persistence candidates, bytes, and requests");
        output.AppendLine();
        output.AppendLine("| Platform | Lifecycle | Representation | n | Median candidate files | Median candidate bytes | Median file requests | Median file-sync ms | Median directory requests | Median directory-sync ms | Compound-temp persist requests |");
        output.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (PlatformData platform in platforms)
        foreach (string lifecycle in Lifecycles)
        foreach (string representation in Representations)
        {
            Dictionary<string, string>[] rows = ValidProductionRows(platform)
                .Where(row => row["lifecycle"] == lifecycle && row["representation"] == representation
                    && row["durable"] == "true").ToArray();
            output.AppendLine($"| {platform.PlatformId} | {lifecycle} | {representation} | {rows.Length} | {Median(rows, "durability_candidate_files")} | {Median(rows, "durability_candidate_bytes")} | {Median(rows, "file_persist_requests")} | {Median(rows, "file_persist_elapsed_ms")} | {Median(rows, "directory_persist_requests")} | {Median(rows, "directory_persist_elapsed_ms")} | {Median(rows, "pack_temp_explicit_persist_requests")} |");
        }
        output.AppendLine();
    }

    private static void AppendPairedSummary(StringBuilder output, IReadOnlyList<PlatformData> platforms)
    {
        output.AppendLine("## Paired durability savings, end-to-end savings, and difference-in-differences");
        output.AppendLine();
        output.AppendLine("Positive savings mean loose took longer than compound. Durability penalty is durable commit-call time minus non-durable commit-call time, paired by launch, lifecycle, and observation number.");
        output.AppendLine();
        output.AppendLine("| Platform | Lifecycle | Metric | n | Median | Q1 | Q3 | IQR | Launch-cluster bootstrap 95% CI | Launch medians |");
        output.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---|---|");
        foreach (PlatformData platform in platforms)
        foreach (string lifecycle in Lifecycles)
        foreach (string metric in PairedMetricNames.Where(static metric => metric != "compound_pack_ms"))
        {
            MetricSummary stats = Summarise(BuildLifecycleMetrics(platform, lifecycle)[metric], [1, 2, 3, 4, 5],
                BootstrapSeed(platform.PlatformId, lifecycle, metric));
            output.AppendLine($"| {platform.PlatformId} | {lifecycle} | {metric} | {stats.N} | {F(stats.Median)} | {F(stats.Q1)} | {F(stats.Q3)} | {F(stats.Iqr)} | {stats.CiText} | {JsonSerializer.Serialize(stats.LaunchMedians)} |");
        }
        output.AppendLine();
    }

    private static void AppendLaunchSensitivity(
        StringBuilder output,
        IReadOnlyList<PlatformData> platforms,
        IReadOnlyDictionary<int, string> sensitivity)
    {
        output.AppendLine("## Launch-level and leave-one-launch-out sensitivity");
        output.AppendLine();
        output.AppendLine("| Platform | Lifecycle | Metric | All launches | Omit 1 | Omit 2 | Omit 3 | Omit 4 | Omit 5 |");
        output.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (PlatformData platform in platforms)
        foreach (string lifecycle in Lifecycles)
        foreach (string metric in new[] { "durability_saving_ms", "operation_saving_ms", "durability_penalty_difference_in_differences_ms" })
        {
            List<MetricPoint> points = BuildLifecycleMetrics(platform, lifecycle)[metric];
            string all = F(Summarise(points, [1, 2, 3, 4, 5], 3001).Median);
            string[] omitted = Enumerable.Range(1, 5).Select(launch =>
                F(Summarise(points, Enumerable.Range(1, 5).Where(value => value != launch).ToArray(), 3001 + launch).Median)).ToArray();
            output.AppendLine($"| {platform.PlatformId} | {lifecycle} | {metric} | {all} | {string.Join(" | ", omitted)} |");
        }
        output.AppendLine();
        output.AppendLine("| Omitted production launch | Recomputed overall classification |");
        output.AppendLine("|---:|---|");
        foreach ((int launch, string result) in sensitivity)
            output.AppendLine($"| {launch} | {result} |");
        output.AppendLine();
    }

    private static void AppendSameLifecycleStability(
        StringBuilder output,
        IReadOnlyDictionary<string, string> stableSupportingLifecycle)
    {
        output.AppendLine("## Same-lifecycle support stability");
        output.AppendLine();
        output.AppendLine("A lifecycle is reported only when that same lifecycle has positive all-five-launch medians for durability and operation savings, and both medians remain positive in each of the five outer leave-one-launch-out launch sets.");
        output.AppendLine();
        output.AppendLine("| Platform | Stable supporting lifecycle | All-five and five outer leave-one-launch-out signs positive |");
        output.AppendLine("|---|---|---|");
        foreach ((string platform, string lifecycle) in stableSupportingLifecycle)
            output.AppendLine($"| {platform} | {lifecycle} | {lifecycle != "none"} |");
        output.AppendLine();
    }

    private static void AppendCardinalitySummary(StringBuilder output, IReadOnlyList<PlatformData> platforms)
    {
        output.AppendLine("## Compact cardinality confirmation");
        output.AppendLine();
        output.AppendLine("The 8 MiB and 82 MiB fixed payloads used 1, 4, 16, 64, and 128 objects with equal-size and production-shaped partitions. Production-shaped vectors scale observed loose-member proportions from the measured compound-durable-fresh batch. Exact vectors were saved before execution.");
        output.AppendLine();
        output.AppendLine("| Platform | Payload MiB | Partition mode | Objects | Valid n | Median durability ms | IQR ms | 95% bootstrap CI | Median file requests | Median file-sync ms | Spearman rho | Median monotonic |");
        output.AppendLine("|---|---:|---|---:|---:|---:|---:|---|---:|---:|---:|---|");
        foreach (PlatformData platform in platforms)
        foreach (Dictionary<string, string> row in platform.CardinalitySummaryRows)
            output.AppendLine($"| {platform.PlatformId} | {ParseInt(row["payload_bytes"]) / 1024 / 1024} | {row["partition_mode"]} | {row["object_count"]} | {row["valid_n"]} | {row["median_durability_sync_ms"]} | {Difference(row["q3_ms"], row["q1_ms"])} | [{row["bootstrap_95ci_low_ms"]}, {row["bootstrap_95ci_high_ms"]}] | {row["median_file_persist_requests"]} | {row["median_file_persist_elapsed_ms"]} | {row["spearman_rho"]} | {row["median_non_decreasing"]} |");
        output.AppendLine();
    }

    private static void AppendCorrectnessSummary(StringBuilder output, IReadOnlyList<PlatformData> platforms)
    {
        output.AppendLine("## Correctness and integrity counts");
        output.AppendLine();
        output.AppendLine("| Platform | Production measured | Warm-ups | Valid topology pairs | Topology mismatches | Failed pairs | Reopen failures | ID lookup failures | Integrity failures | Cardinality measured | Payload verification failures |");
        output.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (PlatformData platform in platforms)
            output.AppendLine($"| {platform.PlatformId} | {platform.Production.Count(row => row["warmup"] == "false")} | {platform.Production.Count(row => row["warmup"] == "true")} | {platform.ProductionPairs.Count(row => row["observation"] != "0" && row["pair_status"] == "valid")} | {platform.ProductionPairs.Count(row => row["observation"] != "0" && row["pair_status"] == "topology_mismatch")} | {platform.ProductionPairs.Count(row => row["observation"] != "0" && row["pair_status"] is "observation_failure" or "missing_observation")} | {platform.Production.Count(row => HasCommitTiming(row) && row["reopen_ok"] != "true")} | {platform.Production.Count(row => HasCommitTiming(row) && row["id_lookup_pass"] != "true")} | {platform.Production.Count(row => HasCommitTiming(row) && row["index_integrity_pass"] != "true")} | {platform.Cardinality.Count(row => row["warmup"] == "false")} | {platform.Cardinality.Count(row => row["payload_reconstruction_pass"] != "true" && row["warmup"] == "false")} |");
        output.AppendLine();
    }

    private static object BuildSummaryJson(
        string aggregateRoot,
        IReadOnlyList<PlatformData> platforms,
        string classification,
        string baseClassification,
        string rationale,
        IReadOnlyDictionary<int, string> sensitivity,
        IReadOnlyDictionary<string, string> stableSupportingLifecycle)
        => new
        {
            experiment_sha = ExpectedExperiment(platforms),
            evidence_root = Path.GetFullPath(aggregateRoot),
            classification,
            pre_sensitivity_classification = baseClassification,
            rationale,
            leave_one_launch_out_classification = sensitivity,
            same_lifecycle_stable_supporting_lifecycle = stableSupportingLifecycle,
            previous_classification = "premise_supported",
            agrees_with_previous_classification = classification == "premise_supported",
            cross_platform_dataset_sha256_match = platforms.Select(static platform => platform.CorpusSha256)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1,
            platforms = platforms.Select(platform => new
            {
                platform_id = platform.PlatformId,
                environment = platform.Environment,
                source_and_assembly_hashes = platform.AssemblyHashes,
                dataset_identity = platform.DatasetIdentity,
                canonical_content_sha256 = platform.CorpusSha256,
                baseline_topology_pass = platform.BaselineTopologyPass,
                evidence_ready = platform.DataReady([1, 2, 3, 4, 5]),
                validation_errors = platform.ValidationErrors,
                production_expected_measured = 300,
                production_observed_measured = platform.Production.Count(row => row["warmup"] == "false"),
                production_expected_warmups = 60,
                production_observed_warmups = platform.Production.Count(row => row["warmup"] == "true"),
                cardinality_expected_measured = 300,
                cardinality_observed_measured = platform.Cardinality.Count(row => row["warmup"] == "false"),
                cardinality_expected_warmups = 60,
                cardinality_observed_warmups = platform.Cardinality.Count(row => row["warmup"] == "true"),
                correctness_failure = platform.CorrectnessFailure,
                source_semantics_valid = platform.SourceSemanticsValid,
                cardinality_supports_mechanism = CardinalitySupportsMechanism([platform], [1, 2, 3], requireMonotonic: true),
                lifecycle_metrics = Lifecycles.ToDictionary(lifecycle => lifecycle,
                    lifecycle => BuildLifecycleMetrics(platform, lifecycle).ToDictionary(
                        static pair => pair.Key,
                        pair => Summarise(pair.Value, [1, 2, 3, 4, 5], BootstrapSeed(platform.PlatformId, lifecycle, pair.Key))))
            }).ToArray()
        };

    private static void WriteSha256Manifest(string root)
    {
        string path = Path.Combine(root, "sha256sums.txt");
        string fullManifest = Path.GetFullPath(path);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(file => !Path.GetFullPath(file).Equals(fullManifest, StringComparison.Ordinal))
            .OrderBy(static file => file, StringComparer.Ordinal)
            .ToArray();
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        foreach (string file in files)
            writer.WriteLine($"{HashFile(file)}  {Path.GetRelativePath(root, file).Replace('\\', '/')}");
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static Dictionary<string, string>[] ValidProductionRows(PlatformData platform)
    {
        var validPairKeys = platform.ProductionPairs.Where(static row => row["pair_status"] == "valid")
            .Select(row => (Launch: ParseInt(row["launch"]), Observation: ParseInt(row["observation"]),
                Lifecycle: row["lifecycle"], Durable: ParseBoolean(row["durable"])))
            .ToHashSet();
        return platform.Production.Where(row => row["warmup"] == "false" && string.IsNullOrEmpty(row["error"])
            && row["reopen_ok"] == "true" && row["id_lookup_pass"] == "true"
            && row["index_integrity_pass"] == "true"
            && validPairKeys.Contains((ParseInt(row["launch"]), ParseInt(row["observation"]),
                row["lifecycle"], ParseBoolean(row["durable"]))))
            .ToArray();
    }

    private static string Median(IEnumerable<Dictionary<string, string>> rows, string column)
    {
        double[] values = rows.Select(row => ParseDouble(row[column])).Where(static value => value is not null)
            .Select(static value => value!.Value).Order().ToArray();
        return values.Length == 0 ? "n/a" : F(Quantile(values, 0.5));
    }

    private static string Difference(string right, string left)
        => ParseDouble(right) is double q3 && ParseDouble(left) is double q1 ? F(q3 - q1) : "n/a";

    private static string ExpectedExperiment(IReadOnlyList<PlatformData> platforms)
        => platforms.Select(static platform => platform.ExperimentSha).FirstOrDefault(static sha => sha.Length > 0) ?? string.Empty;

    private static bool HasCommitTiming(IReadOnlyDictionary<string, string> row)
        => ParseDouble(row["commit_call_ms"]) is not null;

    private static bool IsSteadyPrimingObservationValid(IReadOnlyDictionary<string, string> row)
        => ParseBoolean(row.GetValueOrDefault("priming_open_durable", string.Empty))
            && ParseBoolean(row.GetValueOrDefault("priming_durable_baseline_pass", string.Empty))
            && ParseLong(row.GetValueOrDefault("priming_expected_inherited_file_count", string.Empty)) > 0
            && ParseLong(row.GetValueOrDefault("priming_file_persist_requests", string.Empty))
                >= ParseLong(row.GetValueOrDefault("priming_expected_inherited_file_count", string.Empty))
            && ParseLong(row.GetValueOrDefault("priming_directory_persist_requests", string.Empty)) > 0;

    private static int ParseInt(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;

    private static long ParseLong(string value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0L;

    private static double? ParseDouble(string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed)
            ? parsed : null;

    private static double? ParseDoubleDifference(double? left, double? right)
        => left is double leftValue && right is double rightValue ? leftValue - rightValue : null;

    private static bool ParseBoolean(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static string F(double? value)
        => value is null ? "n/a" : value.Value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string ExpectedExperiment(PlatformData platform) => platform.ExperimentSha;
    private sealed record PlatformData(
        string PlatformId,
        string Root,
        Dictionary<string, string>? Environment,
        Dictionary<string, string>? AssemblyHashes,
        Dictionary<string, string>? DatasetIdentity,
        List<Dictionary<string, string>> Production,
        List<Dictionary<string, string>> ProductionPairs,
        List<Dictionary<string, string>> Cardinality,
        List<Dictionary<string, string>> LooseTopology,
        List<Dictionary<string, string>> CompoundTopology,
        List<Dictionary<string, string>> ExecutionOrder,
        List<Dictionary<string, string>> CardinalityOrder,
        List<Dictionary<string, string>> CardinalityPartitions)
    {
        public List<Dictionary<string, string>> CardinalitySummaryRows { get; set; } = [];
        public List<string> ValidationErrors { get; } = [];
        public bool CorrectnessFailure { get; private set; }
        public bool SourceSemanticsValid { get; private set; }
        public bool BaselineTopologyPass { get; private set; }
        public string CorpusSha256 => DatasetIdentity?.GetValueOrDefault("canonical_content_sha256", string.Empty) ?? string.Empty;
        public string ExperimentSha => AssemblyHashes?.GetValueOrDefault("experiment_sha", string.Empty) ?? string.Empty;
        public string SpikeAssemblySha => NestedHash("spike_assembly");
        public string CoreAssemblySha => NestedHash("leancorpus_core_assembly");

        public string EnvironmentValue(string name) => Environment?.GetValueOrDefault(name, string.Empty) ?? string.Empty;
        public string DatasetValue(string name) => DatasetIdentity?.GetValueOrDefault(name, string.Empty) ?? string.Empty;
        public string GenerationValue(string name)
        {
            if (DatasetIdentity is null || !DatasetIdentity.TryGetValue("generation_parameters", out string? raw))
                return string.Empty;
            using JsonDocument document = JsonDocument.Parse(raw);
            return document.RootElement.TryGetProperty(name, out JsonElement value) ? value.ToString() : string.Empty;
        }

        public string TopologyVector(string representation)
        {
            Dictionary<string, string>[] rows = (representation == "loose" ? LooseTopology : CompoundTopology)
                .OrderBy(row => ParseInt(row["segment_ordinal"])).ToArray();
            return JsonSerializer.Serialize(rows.Select(row => new
            {
                min = ParseInt(row["min_document_ordinal"]),
                max = ParseInt(row["max_document_ordinal"]),
                count = ParseInt(row["document_count"])
            }));
        }

        public void Validate(string expectedExperiment)
        {
            if (Environment is null)
                ValidationErrors.Add("environment.json is missing.");
            if (AssemblyHashes is null)
                ValidationErrors.Add("source-and-assembly-hashes.json is missing.");
            if (DatasetIdentity is null)
                ValidationErrors.Add("dataset-identity.json is missing.");
            if (ExperimentSha != expectedExperiment)
                ValidationErrors.Add("Recorded experiment commit does not match the evidence-root name.");
            if (AssemblyHashes?.GetValueOrDefault("working_tree_clean") != "true"
                || Environment?.GetValueOrDefault("workingTreeClean") != "true")
                ValidationErrors.Add("The measured source tree was not recorded clean.");
            string sourceProvenance = AssemblyHashes?.GetValueOrDefault("source_provenance_kind", string.Empty) ?? string.Empty;
            if (sourceProvenance is not ("git-working-tree" or "verified-git-archive"))
                ValidationErrors.Add("Source provenance is missing or unsupported.");
            if (sourceProvenance == "verified-git-archive"
                && (AssemblyHashes?.GetValueOrDefault("source_manifest_sha256", string.Empty)?.Length != 64
                    || AssemblyHashes.GetValueOrDefault("source_archive_sha256", string.Empty).Length != 64))
                ValidationErrors.Add("The verified Windows source archive or manifest SHA-256 is missing.");
            if (DatasetIdentity is not null
                && (DatasetValue("dataForgeVersion").Length == 0 || DatasetValue("profileId") != "leancorpus-search"
                    || DatasetValue("profileVersion") != "1" || GenerationValue("seed") != "42"
                    || GenerationValue("record_count") != "100000" || DatasetValue("dependencies").Length == 0))
                ValidationErrors.Add("DataForge identity does not match the locked profile, seed, count, or dependency requirements.");
            if (CorpusSha256.Length != 64)
                ValidationErrors.Add("Canonical DataForge content SHA-256 is missing or malformed.");
            string expectedFileSystem = PlatformId == "windows-ntfs" ? "NTFS" : "ext4";
            if (!string.Equals(EnvironmentValue("fileSystem"), expectedFileSystem, StringComparison.OrdinalIgnoreCase))
                ValidationErrors.Add($"Recorded filesystem does not match {expectedFileSystem}.");

            ValidateBaselineManifests();
            ValidateProductionOrder();
            ValidateCardinalityPlan();
            ValidateTrialCounts();
            ValidateStopRule();
            CorrectnessFailure = Production.Any(row => HasCommitTiming(row)
                && (row.GetValueOrDefault("reopen_ok") != "true"
                    || row.GetValueOrDefault("id_lookup_pass") != "true"
                    || row.GetValueOrDefault("index_integrity_pass") != "true"));
            if (CorrectnessFailure)
                ValidationErrors.Add("At least one measured production observation failed reopen, ID lookup, or deep index integrity validation.");
            int invalidSteadyPriming = Production.Count(row => row.GetValueOrDefault("lifecycle") == "reopened-steady"
                && !IsSteadyPrimingObservationValid(row));
            if (invalidSteadyPriming > 0)
                ValidationErrors.Add($"{invalidSteadyPriming} reopened-steady production observations failed writer-open or inherited-file priming validation.");
            SourceSemanticsValid = Production.All(row => ParseInt(row.GetValueOrDefault("pack_temp_explicit_persist_requests", "0")) == 0);
            if (!SourceSemanticsValid)
                ValidationErrors.Add("An explicit persistence request was observed for the compound temporary output.");
            string summaryPath = Path.Combine(Root, "cardinality-summary.csv");
            CardinalitySummaryRows = File.Exists(summaryPath) ? Csv.Read(summaryPath) : [];
        }

        public bool DataReady(IReadOnlyList<int> launches)
            => ValidationErrors.Count == 0 && !CorrectnessFailure && SourceSemanticsValid
                && ProductionCellCoverage(launches) && CardinalityCellCoverage([1, 2, 3])
                && BaselineTopologyPass;

        private string NestedHash(string name)
        {
            if (AssemblyHashes is null || !AssemblyHashes.TryGetValue(name, out string? raw))
                return string.Empty;
            using JsonDocument document = JsonDocument.Parse(raw);
            return document.RootElement.TryGetProperty("sha256", out JsonElement hash) ? hash.GetString() ?? string.Empty : string.Empty;
        }

        private void ValidateBaselineManifests()
        {
            BaselineTopologyPass = BaselinePass(LooseTopology, "loose") && BaselinePass(CompoundTopology, "compound")
                && LooseTopology.Count == CompoundTopology.Count
                && LooseTopology.OrderBy(row => ParseInt(row["segment_ordinal"]))
                    .Zip(CompoundTopology.OrderBy(row => ParseInt(row["segment_ordinal"])))
                    .All(static pair => pair.First["min_document_ordinal"] == pair.Second["min_document_ordinal"]
                        && pair.First["max_document_ordinal"] == pair.Second["max_document_ordinal"]
                        && pair.First["document_count"] == pair.Second["document_count"]);
            if (!BaselineTopologyPass)
                ValidationErrors.Add("Loose and compound baseline topology manifests do not encode the same nine exact logical segments.");
        }

        private static bool BaselinePass(IReadOnlyList<Dictionary<string, string>> rows, string representation)
        {
            if (rows.Count != 9)
                return false;
            for (int index = 0; index < 9; index++)
            {
                Dictionary<string, string>? row = rows.SingleOrDefault(row => ParseInt(row["segment_ordinal"]) == index);
                if (row is null || row["representation"] != representation
                    || ParseInt(row["min_document_ordinal"]) != index * 10_000
                    || ParseInt(row["max_document_ordinal"]) != index * 10_000 + 9_999
                    || ParseInt(row["document_count"]) != 10_000
                    || string.IsNullOrWhiteSpace(row["segment_id"])
                    || ParseInt(row["physical_file_count"]) <= 0
                    || ParseLong(row["physical_bytes"]) <= 0)
                    return false;
            }
            return true;
        }

        private void ValidateProductionOrder()
        {
            int[] seeds = [20261201, 20261202, 20261203, 20261204, 20261205];
            string[] expected = (from representation in Representations
                                 from durability in new[] { "disabled", "enabled" }
                                 from lifecycle in Lifecycles
                                 select $"{representation}-{durability}-{lifecycle}")
                .OrderBy(static cell => cell, StringComparer.Ordinal).ToArray();
            for (int launch = 1; launch <= 5; launch++)
            {
                Dictionary<string, string>[] rows = ExecutionOrder.Where(row => ParseInt(row["launch"]) == launch).ToArray();
                if (rows.Length != 12 || rows.Select(static row => row["cell_id"]).Distinct(StringComparer.Ordinal).Count() != 12
                    || rows.Any(row => ParseInt(row["shuffle_seed"]) != seeds[launch - 1])
                    || !rows.Select(static row => row["cell_id"]).Order(StringComparer.Ordinal).SequenceEqual(expected, StringComparer.Ordinal))
                    ValidationErrors.Add($"Production order for launch {launch} is incomplete or uses a different cell list/seed.");
            }
        }

        private void ValidateCardinalityPlan()
        {
            if (CardinalityOrder.Count != 60 || CardinalityPartitions.Count != 20)
            {
                ValidationErrors.Add("Cardinality execution order or partition plan has the wrong cell count.");
                return;
            }
            for (int launch = 1; launch <= 3; launch++)
            {
                Dictionary<string, string>[] rows = CardinalityOrder.Where(row => ParseInt(row["launch"]) == launch).ToArray();
                if (rows.Length != 20 || rows.Select(row => (row["payload_bytes"], row["object_count"], row["partition_mode"]))
                        .Distinct().Count() != 20)
                    ValidationErrors.Add($"Cardinality order for launch {launch} is incomplete or duplicated.");
            }
            foreach (Dictionary<string, string> row in CardinalityPartitions)
            {
                long[] vector = JsonSerializer.Deserialize<long[]>(row["partition_vector_bytes"]) ?? [];
                if (vector.Length != ParseInt(row["object_count"]) || vector.Sum() != ParseLong(row["payload_bytes"])
                    || HashUtf8(JsonSerializer.Serialize(vector)) != row["partition_vector_sha256"])
                    ValidationErrors.Add("A saved cardinality partition vector failed count, total, or hash validation.");
            }
        }

        private void ValidateTrialCounts()
        {
            if (Production.Count != 360 || Cardinality.Count != 360)
                ValidationErrors.Add($"Raw trial counts are production={Production.Count}/360 and cardinality={Cardinality.Count}/360.");
            if (Production.Select(row => (row["launch"], row["cell_order"], row["observation"])).Distinct().Count() != Production.Count)
                ValidationErrors.Add("Production raw data contain duplicate launch/cell/observation keys.");
            if (Cardinality.Select(row => (row["launch"], row["cell_order"], row["observation"])).Distinct().Count() != Cardinality.Count)
                ValidationErrors.Add("Cardinality raw data contain duplicate launch/cell/observation keys.");
            if (ProductionPairs.Count != 180)
                ValidationErrors.Add($"Production pairs contain {ProductionPairs.Count} rows; expected 180 including warm-ups.");
        }

        private void ValidateStopRule()
        {
            foreach (var group in ProductionPairs.Where(row => row["observation"] != "0" && row["pair_status"] != "valid")
                         .GroupBy(row => (Launch: row["launch"], Lifecycle: row["lifecycle"], Durable: row["durable"])))
                if (group.Count() > 1)
                    ValidationErrors.Add($"More than one invalid pair in launch={group.Key.Launch}, lifecycle={group.Key.Lifecycle}, durable={group.Key.Durable}.");
            foreach (var group in Cardinality.Where(row => row["warmup"] == "false" && !string.IsNullOrEmpty(row["error"]))
                         .GroupBy(row => (Launch: row["launch"], Cell: row["cell_order"])))
                if (group.Count() > 1)
                    ValidationErrors.Add($"More than one failed cardinality observation in launch={group.Key.Launch}, cell={group.Key.Cell}.");
        }

        private bool ProductionCellCoverage(IReadOnlyList<int> launches)
        {
            foreach (int launch in launches)
            foreach (string lifecycle in Lifecycles)
            foreach (bool durable in DurabilityLevels)
            {
                Dictionary<string, string>[] rows = ProductionPairs.Where(row => ParseInt(row["launch"]) == launch
                    && row["lifecycle"] == lifecycle && ParseBoolean(row["durable"]) == durable
                    && row["observation"] != "0").ToArray();
                if (rows.Length != 5 || rows.Count(static row => row["pair_status"] == "valid") < 4)
                    return false;
            }
            return true;
        }

        private bool CardinalityCellCoverage(IReadOnlyList<int> launches)
        {
            foreach (int launch in launches)
            foreach (int payloadBytes in Dataset.CardinalityPayloadSizes)
            foreach (int objectCount in CardinalityCounts)
            foreach (string mode in CardinalityModes)
            {
                Dictionary<string, string>[] rows = Cardinality.Where(row => ParseInt(row["launch"]) == launch
                    && ParseInt(row["payload_bytes"]) == payloadBytes && ParseInt(row["object_count"]) == objectCount
                    && row["partition_mode"] == mode && row["warmup"] == "false").ToArray();
                if (rows.Length != 5 || rows.Count(static row => row["payload_reconstruction_pass"] == "true" && string.IsNullOrEmpty(row["error"])) < 4)
                    return false;
            }
            return true;
        }
    }

    private sealed record MetricPoint(int Launch, double Value);

    private sealed record MetricSummary(
        int N,
        double? Median,
        double? Q1,
        double? Q3,
        double? Iqr,
        double? CiLow,
        double? CiHigh,
        double? Minimum,
        double? Maximum,
        IReadOnlyDictionary<string, double?> LaunchMedians)
    {
        public string CiText => CiLow is null || CiHigh is null ? "n/a" : $"[{F(CiLow)}, {F(CiHigh)}]";
        public static MetricSummary Empty { get; } = new(0, null, null, null, null, null, null, null, null,
            new Dictionary<string, double?>());
    }
}
