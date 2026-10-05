using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rowles.LeanCorpus.CompoundDurabilitySpike;

internal static partial class SpikeRunner
{
    private static readonly string[] PrimaryPlatforms = ["linux-ext4", "windows-ntfs"];
    private static readonly string[] Representations = ["loose", "compound"];
    private static readonly string[] Lifecycles = ["fresh", "reopened"];
    private static readonly bool[] DurabilityLevels = [false, true];

    public static Task<int> AnalyseAsync(string root, Arguments arguments)
    {
        string aggregateRoot = Path.GetFullPath(root);
        Directory.CreateDirectory(aggregateRoot);
        var platformData = PrimaryPlatforms.Select(platform => LoadPlatform(aggregateRoot, platform)).ToArray();
        string baseSha = ReadFirst(platformData, "base-sha.txt");
        string spikeSha = ReadFirst(platformData, "spike-sha.txt");
        string startedUtc = ReadFirst(platformData, "started-utc.txt");
        string corpusSha = ReadCorpusHash(platformData);
        var bootstrap = new Random(20261004);
        var summaries = new StringBuilder();

        AppendFullProductionStatistics(summaries, platformData, bootstrap);
        AppendCardinalityStatistics(summaries, platformData, bootstrap);
        var comparison = BuildMatchedComparisons(platformData, bootstrap);
        AppendComparisonStatistics(summaries, comparison, bootstrap);

        bool hasMissingPlatform = platformData.Any(static platform => !platform.HasEnvironment);
        bool anyInvalidPartial = platformData.SelectMany(static platform => platform.Recovery)
            .Any(static row => row["recovered_generation_class"] == "invalid_partial");
        bool anyUnopenable = platformData.SelectMany(static platform => platform.Recovery)
            .Any(static row => row["recovered_generation_class"] == "unopenable");
        bool anyPostReturnViolation = platformData.SelectMany(static platform => platform.Recovery)
            .Any(static row => row["checkpoint"] == "after_commit_return"
                && row["termination_kind"] != "not_applicable"
                && (row["recovered_generation_class"] != "new_complete" || ParseLong(row["tmp_file_count"]) != 0));
        bool anyMissingOrTruncated = platformData.SelectMany(static platform => platform.Recovery)
            .Any(static row => ParseLong(row["missing_file_count"]) > 0 || ParseLong(row["truncated_file_count"]) > 0);
        bool correctnessFailure = anyInvalidPartial || anyUnopenable || anyPostReturnViolation || anyMissingOrTruncated;
        var qualification = platformData.ToDictionary(
            static platform => platform.PlatformId,
            platform => Qualify(platform, [1, 2, 3]),
            StringComparer.Ordinal);
        string classification = Classify(platformData, qualification, hasMissingPlatform, correctnessFailure);
        var launchSensitivity = new SortedDictionary<int, string>();
        if (!correctnessFailure)
        {
            foreach (int omittedLaunch in new[] { 1, 2, 3 })
            {
                int[] remainingLaunches = new[] { 1, 2, 3 }.Where(launch => launch != omittedLaunch).ToArray();
                PlatformData[] reducedPlatforms = platformData.Select(platform => platform with
                {
                    Production = platform.Production.Where(row => ParseInt(row["launch"]) != omittedLaunch).ToList(),
                    Cardinality = platform.Cardinality.Where(row => ParseInt(row["launch"]) != omittedLaunch).ToList()
                }).ToArray();
                var reducedQualification = reducedPlatforms.ToDictionary(
                    static platform => platform.PlatformId,
                    platform => Qualify(platform, remainingLaunches),
                    StringComparer.Ordinal);
                launchSensitivity[omittedLaunch] = Classify(
                    reducedPlatforms,
                    reducedQualification,
                    hasMissingPlatform,
                    correctnessFailure: false);
            }
        }
        bool sensitivityChanged = !correctnessFailure
            && launchSensitivity.Values.Any(result => !string.Equals(result, classification, StringComparison.Ordinal));
        if (sensitivityChanged)
            classification = "inconclusive";
        bool proceed = classification is "premise_supported" or "platform_specific" or "mechanism_present_but_packing_cancels";
        int correctnessFailures = CountCorrectnessFailures(platformData);

        AppendMandatoryTables(summaries, platformData, comparison, bootstrap);
        AppendClassification(summaries, classification, proceed, qualification, platformData, launchSensitivity, sensitivityChanged);
        WriteDoseResponseSvg(aggregateRoot, platformData, bootstrap);
        string completedUtc = SpikeInfrastructure.CurrentUtc();

        var platformSummary = platformData.Select(platform => new
        {
            platform_id = platform.PlatformId,
            environment_id = platform.EnvironmentId,
            status = !platform.HasEnvironment ? "missing" : qualification[platform.PlatformId].Valid ? "complete" : "incomplete",
            production_cells_completed = CountCompletedProductionCells(platform),
            cardinality_cells_completed = CountCompletedCardinalityCells(platform),
            recovery_trials_completed = platform.Recovery.Count(static row => row["termination_kind"] != "not_applicable"),
            premise_criteria_pass = qualification[platform.PlatformId].PremiseCriteriaPass,
            premise_supported = qualification[platform.PlatformId].PremiseSupported,
            durable_cardinality_spearman = qualification[platform.PlatformId].Spearman
        }).ToArray();

        int productionCellsCompleted = platformData.Sum(CountCompletedProductionCells);
        int cardinalityCellsCompleted = platformData.Sum(CountCompletedCardinalityCells);
        int recoveryTrialsCompleted = platformData.Sum(platform =>
            platform.Recovery.Count(static row => row["termination_kind"] != "not_applicable"));
        var summaryJson = new
        {
            base_sha = baseSha,
            spike_sha = spikeSha,
            started_utc = startedUtc,
            completed_utc = completedUtc,
            platforms = platformSummary,
            corpus_sha256 = corpusSha,
            production_cells_expected = 16,
            production_cells_completed = productionCellsCompleted,
            cardinality_cells_expected = 28,
            cardinality_cells_completed = cardinalityCellsCompleted,
            recovery_trials_expected = 90,
            recovery_trials_completed = recoveryTrialsCompleted,
            correctness_failures = correctnessFailures,
            classification,
            proceed_to_main_study = proceed,
            rationale = BuildRationale(classification, qualification, hasMissingPlatform, correctnessFailures, sensitivityChanged),
            artefact_sha256_file = "sha256sums.txt"
        };
        File.WriteAllText(Path.Combine(aggregateRoot, "summary.md"), summaries.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(aggregateRoot, "summary.json"),
            JsonSerializer.Serialize(summaryJson, new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false));
        WriteSha256Manifest(aggregateRoot, platformData);
        Console.WriteLine($"classification={classification} proceed_to_main_study={proceed}");
        return Task.FromResult(0);
    }

    private static PlatformData LoadPlatform(string aggregateRoot, string platformId)
    {
        string root = Path.Combine(aggregateRoot, platformId);
        bool hasEnvironment = File.Exists(Path.Combine(root, "environment.json"));
        string environmentId = string.Empty;
        string missingReason = string.Empty;
        if (hasEnvironment)
        {
            using var environment = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "environment.json")));
            environmentId = environment.RootElement.TryGetProperty("environmentId", out var id)
                ? id.GetString() ?? string.Empty
                : string.Empty;
            if (environment.RootElement.TryGetProperty("status", out var status)
                && string.Equals(status.GetString(), "missing", StringComparison.OrdinalIgnoreCase))
                hasEnvironment = false;
        }
        else
        {
            missingReason = $"No {platformId} environment.json was produced.";
        }

        string missingPath = Path.Combine(root, "missing-platform.md");
        if (File.Exists(missingPath))
            missingReason = File.ReadAllText(missingPath).Trim();

        return new PlatformData(
            platformId,
            root,
            hasEnvironment,
            environmentId,
            missingReason,
            ReadIfExists(Path.Combine(root, "production-trials.csv")),
            ReadIfExists(Path.Combine(root, "cardinality-trials.csv")),
            ReadIfExists(Path.Combine(root, "recovery-trials.csv")));
    }

    private static List<Dictionary<string, string>> ReadIfExists(string path)
        => File.Exists(path) ? Csv.Read(path) : [];

    private static void AppendFullProductionStatistics(
        StringBuilder output,
        IReadOnlyList<PlatformData> platforms,
        Random bootstrap)
    {
        output.AppendLine("# Spike 1: Compound Packing and Causal Mechanism");
        output.AppendLine();
        output.AppendLine("## Full production-cell timing distributions");
        output.AppendLine();
        output.AppendLine("Warm-up rows are excluded. Every measured row with a numeric value is retained, including flagged observations; no outliers are removed.");
        output.AppendLine();
        output.AppendLine("| Platform | Lifecycle | Representation | Durable | Metric | n | Median ms | Q1 ms | Q3 ms | IQR ms | 95% bootstrap CI ms | Min ms | Max ms |");
        output.AppendLine("|---|---|---|---:|---|---:|---:|---:|---:|---:|---|---:|---:|");
        string[] metrics =
        [
            "index_ms", "forced_flush_ms", "compound_pack_ms", "metadata_prepare_ms",
            "durability_sync_ms", "post_commit_ms", "commit_call_ms", "operation_ms"
        ];
        foreach (PlatformData platform in platforms)
        foreach (string lifecycle in Lifecycles)
        foreach (string representation in Representations)
        foreach (bool durable in DurabilityLevels)
        foreach (string metric in metrics)
        {
            Dictionary<string, string>[] rows = platform.Production
                .Where(row => IsMeasured(row)
                    && row["lifecycle"] == lifecycle
                    && row["representation"] == representation
                    && ParseBoolean(row["durable"]) == durable)
                .ToArray();
            StatSummary stats = Statistics.Summarise(rows.Select(row => ParseDouble(row[metric])).Where(static value => value.HasValue)
                .Select(static value => value!.Value), bootstrap);
            output.AppendLine($"| {platform.PlatformId} | {lifecycle} | {representation} | {durable} | {metric} | {stats.N} | {F(stats.Median)} | {F(stats.Q1)} | {F(stats.Q3)} | {F(stats.Iqr)} | {stats.CiText} | {F(stats.Minimum)} | {F(stats.Maximum)} |");
        }
        output.AppendLine();
    }

    private static void AppendCardinalityStatistics(
        StringBuilder output,
        IReadOnlyList<PlatformData> platforms,
        Random bootstrap)
    {
        output.AppendLine("## Full cardinality-cell timing distributions");
        output.AppendLine();
        output.AppendLine("| Platform | Durable | Object count | Metric | n | Median ms | Q1 ms | Q3 ms | IQR ms | 95% bootstrap CI ms | Min ms | Max ms |");
        output.AppendLine("|---|---:|---:|---|---:|---:|---:|---:|---:|---|---:|---:|");
        foreach (PlatformData platform in platforms)
        foreach (bool durable in DurabilityLevels)
        foreach (int count in CardinalityCounts)
        {
            Dictionary<string, string>[] rows = platform.Cardinality
                .Where(row => IsMeasured(row) && ParseBoolean(row["durable"]) == durable && ParseInt(row["object_count"]) == count)
                .ToArray();
            foreach (string metric in new[] { "durability_sync_ms", "file_persist_elapsed_ms", "directory_persist_elapsed_ms" })
            {
                StatSummary stats = Statistics.Summarise(rows.Select(row => ParseDouble(row[metric]))
                    .Where(static value => value.HasValue).Select(static value => value!.Value), bootstrap);
                output.AppendLine($"| {platform.PlatformId} | {durable} | {count} | {metric} | {stats.N} | {F(stats.Median)} | {F(stats.Q1)} | {F(stats.Q3)} | {F(stats.Iqr)} | {stats.CiText} | {F(stats.Minimum)} | {F(stats.Maximum)} |");
            }
        }
        output.AppendLine();
    }

    private static Dictionary<(string Platform, string Lifecycle), MatchedComparison> BuildMatchedComparisons(
        IReadOnlyList<PlatformData> platforms,
        Random bootstrap)
    {
        var result = new Dictionary<(string Platform, string Lifecycle), MatchedComparison>();
        foreach (PlatformData platform in platforms)
        foreach (string lifecycle in Lifecycles)
        {
            var production = platform.Production.Where(row => IsMeasured(row) && row["lifecycle"] == lifecycle).ToDictionary(
                row => (ParseInt(row["launch"]), ParseInt(row["observation"]), row["representation"], ParseBoolean(row["durable"])),
                row => row);
            var loosePenalty = new List<double>();
            var compoundPenalty = new List<double>();
            var did = new List<double>();
            var syncSaving = new List<double>();
            var syncSavingPercent = new List<double>();
            var operationSaving = new List<double>();
            foreach (int launch in new[] { 1, 2, 3 })
            for (int observation = 1; observation <= 5; observation++)
            {
                bool TryGet(string representation, bool durable, out Dictionary<string, string> row)
                    => production.TryGetValue((launch, observation, representation, durable), out row!);
                if (TryGet("loose", true, out var looseDurable) && TryGet("loose", false, out var looseNonDurable)
                    && TryGet("compound", true, out var compoundDurable) && TryGet("compound", false, out var compoundNonDurable)
                    && looseDurable["lifecycle"] == lifecycle && looseNonDurable["lifecycle"] == lifecycle
                    && compoundDurable["lifecycle"] == lifecycle && compoundNonDurable["lifecycle"] == lifecycle)
                {
                    double? looseDurableCommit = ParseDouble(looseDurable["commit_call_ms"]);
                    double? looseNonDurableCommit = ParseDouble(looseNonDurable["commit_call_ms"]);
                    double? compoundDurableCommit = ParseDouble(compoundDurable["commit_call_ms"]);
                    double? compoundNonDurableCommit = ParseDouble(compoundNonDurable["commit_call_ms"]);
                    double? looseDurability = ParseDouble(looseDurable["durability_sync_ms"]);
                    double? compoundDurability = ParseDouble(compoundDurable["durability_sync_ms"]);
                    double? looseOperation = ParseDouble(looseDurable["operation_ms"]);
                    double? compoundOperation = ParseDouble(compoundDurable["operation_ms"]);
                    if (looseDurableCommit is null || looseNonDurableCommit is null
                        || compoundDurableCommit is null || compoundNonDurableCommit is null
                        || looseDurability is null || compoundDurability is null
                        || looseOperation is null || compoundOperation is null)
                        continue;

                    double looseDelta = looseDurableCommit.Value - looseNonDurableCommit.Value;
                    double compoundDelta = compoundDurableCommit.Value - compoundNonDurableCommit.Value;
                    loosePenalty.Add(looseDelta);
                    compoundPenalty.Add(compoundDelta);
                    did.Add(looseDelta - compoundDelta);
                    double saving = looseDurability.Value - compoundDurability.Value;
                    syncSaving.Add(saving);
                    double looseSync = looseDurability.Value;
                    syncSavingPercent.Add(looseSync == 0 ? 0 : saving / looseSync * 100d);
                    operationSaving.Add(looseOperation.Value - compoundOperation.Value);
                }
            }
            result[(platform.PlatformId, lifecycle)] = new MatchedComparison(
                Statistics.Summarise(loosePenalty, bootstrap),
                Statistics.Summarise(compoundPenalty, bootstrap),
                Statistics.Summarise(did, bootstrap),
                Statistics.Summarise(syncSaving, bootstrap),
                Statistics.Summarise(syncSavingPercent, bootstrap),
                Statistics.Summarise(operationSaving, bootstrap));
        }
        return result;
    }

    private static void AppendComparisonStatistics(
        StringBuilder output,
        IReadOnlyDictionary<(string Platform, string Lifecycle), MatchedComparison> comparisons,
        Random bootstrap)
    {
        _ = bootstrap;
        output.AppendLine("## Matched mechanism distributions");
        output.AppendLine();
        output.AppendLine("| Platform | Lifecycle | Metric | n | Median | Q1 | Q3 | IQR | 95% bootstrap CI | Min | Max |");
        output.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---|---:|---:|");
        foreach (var ((platform, lifecycle), comparison) in comparisons.OrderBy(static pair => pair.Key.Platform, StringComparer.Ordinal)
                     .ThenBy(static pair => pair.Key.Lifecycle, StringComparer.Ordinal))
        {
            AppendDistribution(output, platform, lifecycle, "added_durability_penalty_ms_loose", comparison.LoosePenalty);
            AppendDistribution(output, platform, lifecycle, "added_durability_penalty_ms_compound", comparison.CompoundPenalty);
            AppendDistribution(output, platform, lifecycle, "DiD_ms", comparison.DifferenceInDifferences);
            AppendDistribution(output, platform, lifecycle, "durability_saving_ms", comparison.DurabilitySaving);
            AppendDistribution(output, platform, lifecycle, "durability_saving_percent", comparison.DurabilitySavingPercent);
            AppendDistribution(output, platform, lifecycle, "net_operation_saving_ms", comparison.OperationSaving);
        }
        output.AppendLine();
    }

    private static void AppendDistribution(StringBuilder output, string platform, string lifecycle, string metric, StatSummary stats)
        => output.AppendLine($"| {platform} | {lifecycle} | {metric} | {stats.N} | {F(stats.Median)} | {F(stats.Q1)} | {F(stats.Q3)} | {F(stats.Iqr)} | {stats.CiText} | {F(stats.Minimum)} | {F(stats.Maximum)} |");

    private static void AppendMandatoryTables(
        StringBuilder output,
        IReadOnlyList<PlatformData> platforms,
        IReadOnlyDictionary<(string Platform, string Lifecycle), MatchedComparison> comparisons,
        Random bootstrap)
    {
        output.AppendLine("## Table A: production matrix");
        output.AppendLine();
        output.AppendLine("| Platform | Lifecycle | Representation | Durable | n | Median operation ms | IQR | Median commit ms | Median durability ms | Median persistence files | Median persistence requests | Median pack ms |");
        output.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (PlatformData platform in platforms)
        foreach (string lifecycle in Lifecycles)
        foreach (string representation in Representations)
        foreach (bool durable in DurabilityLevels)
        {
            var rows = platform.Production.Where(row => IsMeasured(row)
                && row["lifecycle"] == lifecycle && row["representation"] == representation
                && ParseBoolean(row["durable"]) == durable).ToArray();
            StatSummary operation = Statistics.Summarise(rows.Select(row => ParseDouble(row["operation_ms"]))
                .Where(static value => value.HasValue).Select(static value => value!.Value));
            StatSummary commit = Statistics.Summarise(rows.Select(row => ParseDouble(row["commit_call_ms"]))
                .Where(static value => value.HasValue).Select(static value => value!.Value));
            StatSummary durability = Statistics.Summarise(rows.Select(row => ParseDouble(row["durability_sync_ms"]))
                .Where(static value => value.HasValue).Select(static value => value!.Value));
            StatSummary requests = Statistics.Summarise(rows.Select(row => ParseDouble(row["file_persist_requests"]))
                .Where(static value => value.HasValue).Select(static value => value!.Value));
            StatSummary files = Statistics.Summarise(rows.Select(row => ParseDouble(row["durability_candidate_files"]))
                .Where(static value => value.HasValue).Select(static value => value!.Value));
            StatSummary pack = Statistics.Summarise(rows.Select(row => ParseDouble(row["compound_pack_ms"]))
                .Where(static value => value.HasValue).Select(static value => value!.Value));
            output.AppendLine($"| {platform.PlatformId} | {lifecycle} | {representation} | {durable} | {operation.N} | {F(operation.Median)} | {F(operation.Iqr)} | {F(commit.Median)} | {F(durability.Median)} | {F(files.Median)} | {F(requests.Median)} | {F(pack.Median)} |");
        }

        output.AppendLine();
        output.AppendLine("## Table B: mechanism comparison");
        output.AppendLine();
        output.AppendLine("| Platform | Lifecycle | Loose durability median ms | Compound durability median ms | Saving ms | Saving % | Loose request median | Compound request median | DiD median ms | Net operation saving median ms |");
        output.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (PlatformData platform in platforms)
        foreach (string lifecycle in Lifecycles)
        {
            StatSummary loose = ProductionMetric(platform, lifecycle, "loose", true, "durability_sync_ms");
            StatSummary compound = ProductionMetric(platform, lifecycle, "compound", true, "durability_sync_ms");
            StatSummary looseRequests = ProductionMetric(platform, lifecycle, "loose", true, "file_persist_requests");
            StatSummary compoundRequests = ProductionMetric(platform, lifecycle, "compound", true, "file_persist_requests");
            MatchedComparison match = comparisons[(platform.PlatformId, lifecycle)];
            output.AppendLine($"| {platform.PlatformId} | {lifecycle} | {F(loose.Median)} | {F(compound.Median)} | {F(match.DurabilitySaving.Median)} | {F(match.DurabilitySavingPercent.Median)} | {F(looseRequests.Median)} | {F(compoundRequests.Median)} | {F(match.DifferenceInDifferences.Median)} | {F(match.OperationSaving.Median)} |");
        }

        output.AppendLine();
        output.AppendLine("## Table C: cardinality sweep");
        output.AppendLine();
        output.AppendLine("| Platform | Durable | Object count | n | Median durability ms | IQR | 95% CI | Median file requests | Median file request time ms |");
        output.AppendLine("|---|---:|---:|---:|---:|---:|---|---:|---:|");
        foreach (PlatformData platform in platforms)
        foreach (bool durable in DurabilityLevels)
        foreach (int count in CardinalityCounts)
        {
            var rows = platform.Cardinality.Where(row => IsMeasured(row)
                && ParseBoolean(row["durable"]) == durable && ParseInt(row["object_count"]) == count).ToArray();
            StatSummary time = Statistics.Summarise(rows.Select(row => ParseDouble(row["durability_sync_ms"]))
                .Where(static value => value.HasValue).Select(static value => value!.Value), bootstrap);
            StatSummary requests = Statistics.Summarise(rows.Select(row => ParseDouble(row["file_persist_requests"]))
                .Where(static value => value.HasValue).Select(static value => value!.Value));
            StatSummary requestTime = Statistics.Summarise(rows.Select(row => ParseDouble(row["file_persist_elapsed_ms"]))
                .Where(static value => value.HasValue).Select(static value => value!.Value));
            output.AppendLine($"| {platform.PlatformId} | {durable} | {count} | {time.N} | {F(time.Median)} | {F(time.Iqr)} | {time.CiText} | {F(requests.Median)} | {F(requestTime.Median)} |");
        }

        output.AppendLine();
        output.AppendLine("## Table D: recovery");
        output.AppendLine();
        output.AppendLine("| Platform | Representation | Checkpoint | Trials | Previous complete | New complete | Invalid partial | Unopenable | Pass |");
        output.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---|");
        foreach (PlatformData platform in platforms)
        foreach (string representation in Representations)
        foreach (string checkpoint in RecoveryCheckpoints)
        {
            var rows = platform.Recovery.Where(row => row["representation"] == representation && row["checkpoint"] == checkpoint).ToArray();
            int trials = rows.Count(static row => row["termination_kind"] != "not_applicable");
            int previous = rows.Count(static row => row["recovered_generation_class"] == "previous_complete");
            int newer = rows.Count(static row => row["recovered_generation_class"] == "new_complete");
            int partial = rows.Count(static row => row["recovered_generation_class"] == "invalid_partial");
            int unopenable = rows.Count(static row => row["recovered_generation_class"] == "unopenable");
            bool na = rows.Any(static row => row["termination_kind"] == "not_applicable");
            bool pass = rows.Length > 0 && rows.All(static row => string.IsNullOrEmpty(row["error"]))
                && partial == 0 && unopenable == 0
                && (!rows.Any(static row => row["checkpoint"] == "after_commit_return") || newer == trials);
            string passText = na && trials == 0 ? "not_applicable" : rows.Length == 0 ? "missing" : pass ? "yes" : "no";
            output.AppendLine($"| {platform.PlatformId} | {representation} | {checkpoint} | {trials} | {previous} | {newer} | {partial} | {unopenable} | {passText} |");
        }
        output.AppendLine();
        output.AppendLine("## Cardinality dose-response statistics");
        output.AppendLine();
        output.AppendLine("| Platform | Durable | Spearman rho | OLS slope ms/object | File requests non-decreasing with object count |");
        output.AppendLine("|---|---:|---:|---:|---|");
        foreach (PlatformData platform in platforms)
        foreach (bool durable in DurabilityLevels)
        {
            var rows = platform.Cardinality.Where(row => IsMeasured(row) && ParseBoolean(row["durable"]) == durable).ToArray();
            double[] x = rows.Select(row => (double)ParseInt(row["object_count"])).ToArray();
            double[] y = rows.Select(row => ParseDouble(row["durability_sync_ms"]) ?? 0).ToArray();
            double[] requests = rows.Select(row => ParseDouble(row["file_persist_requests"]) ?? 0).ToArray();
            double rho = Statistics.Spearman(x, y);
            double slope = Statistics.Slope(x, y);
            bool monotonic = RequestsNonDecreasing(platform, durable);
            output.AppendLine($"| {platform.PlatformId} | {durable} | {F(rho)} | {F(slope)} | {monotonic.ToString().ToLowerInvariant()} |");
        }
        output.AppendLine();
    }

    private static PlatformQualification Qualify(
        PlatformData platform,
        IReadOnlyList<int> requiredLaunches)
    {
        bool productionComplete = AllProductionCellsComplete(platform, requiredLaunches);
        bool cardinalityComplete = AllCardinalityCellsComplete(platform, requiredLaunches);
        bool recoveryPass = RecoveryPass(platform);
        bool valid = platform.HasEnvironment && productionComplete && cardinalityComplete && recoveryPass;
        double spearman = Statistics.Spearman(
            platform.Cardinality.Where(row => IsMeasured(row) && ParseBoolean(row["durable"]))
                .Select(row => (double)ParseInt(row["object_count"]))
                .ToArray(),
            platform.Cardinality.Where(row => IsMeasured(row) && ParseBoolean(row["durable"]))
                .Select(row => ParseDouble(row["durability_sync_ms"]) ?? 0)
                .ToArray());
        bool requestDoseResponse = RequestsNonDecreasing(platform, durable: true);
        bool durabilityAndRequestsPass = true;
        bool operationPass = true;
        foreach (string lifecycle in Lifecycles)
        {
            StatSummary loose = ProductionMetric(platform, lifecycle, "loose", true, "durability_sync_ms");
            StatSummary compound = ProductionMetric(platform, lifecycle, "compound", true, "durability_sync_ms");
            StatSummary looseRequests = ProductionMetric(platform, lifecycle, "loose", true, "file_persist_requests");
            StatSummary compoundRequests = ProductionMetric(platform, lifecycle, "compound", true, "file_persist_requests");
            bool saving = loose.N > 0 && compound.N > 0
                && loose.Median - compound.Median >= 1.0
                && loose.Median - compound.Median >= loose.Median * 0.10;
            bool fewerRequests = looseRequests.N > 0 && compoundRequests.N > 0 && compoundRequests.Median < looseRequests.Median;
            durabilityAndRequestsPass &= saving && fewerRequests;

            StatSummary looseOperation = ProductionMetric(platform, lifecycle, "loose", true, "operation_ms");
            StatSummary compoundOperation = ProductionMetric(platform, lifecycle, "compound", true, "operation_ms");
            operationPass &= looseOperation.N > 0 && compoundOperation.N > 0
                && compoundOperation.Median <= looseOperation.Median * 1.05;
        }
        bool criteriaPass = valid && durabilityAndRequestsPass && spearman >= 0.60 && requestDoseResponse;
        return new PlatformQualification(valid, recoveryPass, spearman, criteriaPass, operationPass, criteriaPass && operationPass,
            durabilityAndRequestsPass, requestDoseResponse);
    }

    private static string Classify(
        IReadOnlyList<PlatformData> platforms,
        IReadOnlyDictionary<string, PlatformQualification> qualification,
        bool hasMissingPlatform,
        bool correctnessFailure)
    {
        if (correctnessFailure)
            return "correctness_failure";
        if (hasMissingPlatform)
            return "inconclusive";
        if (platforms.Any(platform => !qualification[platform.PlatformId].Valid))
            return "inconclusive";
        bool[] supported = PrimaryPlatforms.Select(platform => qualification[platform].PremiseSupported).ToArray();
        if (supported.All(static value => value))
            return "premise_supported";

        foreach (PlatformData platform in platforms)
        {
            PlatformQualification q = qualification[platform.PlatformId];
            if (q.PremiseCriteriaPass && !q.OperationCriterionPass)
                return "mechanism_present_but_packing_cancels";
        }

        foreach (PlatformData platform in platforms)
        {
            PlatformQualification q = qualification[platform.PlatformId];
            bool materialSaving = Lifecycles.Any(lifecycle =>
            {
                StatSummary loose = ProductionMetric(platform, lifecycle, "loose", true, "durability_sync_ms");
                StatSummary compound = ProductionMetric(platform, lifecycle, "compound", true, "durability_sync_ms");
                return loose.N > 0 && compound.N > 0
                    && loose.Median - compound.Median >= 1.0
                    && loose.Median - compound.Median >= loose.Median * 0.10;
            });
            if (materialSaving && (q.Spearman < 0.60 || !q.RequestDoseResponsePass))
                return "cardinality_not_explanatory";
        }

        if (supported.Count(static value => value) == 1)
            return "platform_specific";

        bool anyMaterialSavingAndRequestReduction = platforms.Any(platform => Lifecycles.Any(lifecycle =>
            ProductionMetric(platform, lifecycle, "loose", true, "durability_sync_ms") is { N: > 0 } loose
            && ProductionMetric(platform, lifecycle, "compound", true, "durability_sync_ms") is { N: > 0 } compound
            && ProductionMetric(platform, lifecycle, "loose", true, "file_persist_requests") is { N: > 0 } looseRequests
            && ProductionMetric(platform, lifecycle, "compound", true, "file_persist_requests") is { N: > 0 } compoundRequests
            && loose.Median - compound.Median >= 1.0
            && loose.Median - compound.Median >= loose.Median * 0.10
            && compoundRequests.Median < looseRequests.Median));
        return anyMaterialSavingAndRequestReduction ? "inconclusive" : "does_not_reproduce";
    }

    private static bool AllProductionCellsComplete(PlatformData platform, IReadOnlyList<int>? requiredLaunches = null)
    {
        requiredLaunches ??= [1, 2, 3];
        foreach (string representation in Representations)
        foreach (bool durable in DurabilityLevels)
        foreach (string lifecycle in Lifecycles)
        {
            var rows = platform.Production.Where(row => IsMeasured(row)
                && row["representation"] == representation && ParseBoolean(row["durable"]) == durable
                && row["lifecycle"] == lifecycle).ToArray();
            if (rows.Length != requiredLaunches.Count * 5 || rows.Any(static row => !ProductionCorrect(row)))
                return false;
            foreach (int launch in requiredLaunches)
                if (rows.Count(row => ParseInt(row["launch"]) == launch) != 5)
                    return false;
        }
        return true;
    }

    private static bool AllCardinalityCellsComplete(PlatformData platform, IReadOnlyList<int>? requiredLaunches = null)
    {
        requiredLaunches ??= [1, 2, 3];
        foreach (int count in CardinalityCounts)
        foreach (bool durable in DurabilityLevels)
        {
            var rows = platform.Cardinality.Where(row => IsMeasured(row)
                && ParseInt(row["object_count"]) == count && ParseBoolean(row["durable"]) == durable).ToArray();
            if (rows.Length != requiredLaunches.Count * 5 || rows.Any(static row => !string.IsNullOrEmpty(row["error"]) || row["payload_reconstruction_pass"] != "true"))
                return false;
            foreach (int launch in requiredLaunches)
                if (rows.Count(row => ParseInt(row["launch"]) == launch) != 5)
                    return false;
        }
        return true;
    }

    private static bool RecoveryPass(PlatformData platform)
    {
        foreach (string representation in Representations)
        foreach (string checkpoint in RecoveryCheckpoints)
        {
            var rows = platform.Recovery.Where(row => row["representation"] == representation && row["checkpoint"] == checkpoint).ToArray();
            if (IsCheckpointNotApplicable(representation, checkpoint))
            {
                if (rows.Length != 1 || rows[0]["termination_kind"] != "not_applicable")
                    return false;
                continue;
            }
            if (rows.Length != 3 || rows.Any(static row => !string.IsNullOrEmpty(row["error"])))
                return false;
            foreach (var row in rows)
            {
                string outcome = row["recovered_generation_class"];
                if (outcome is not ("previous_complete" or "new_complete"))
                    return false;
                if (checkpoint == "after_commit_return" && outcome != "new_complete")
                    return false;
                if (checkpoint == "after_commit_return" && ParseLong(row["tmp_file_count"]) != 0)
                    return false;
                if (ParseLong(row["missing_file_count"]) != 0 || ParseLong(row["truncated_file_count"]) != 0)
                    return false;
            }
        }
        return true;
    }

    private static bool IsCheckpointNotApplicable(string representation, string checkpoint)
        => checkpoint == "after_commit_marker_final_persisted"
           || representation == "loose" && checkpoint is "after_compound_tmp_close_before_rename" or "after_compound_rename" or "after_loose_members_deleted";

    private static bool RequestsNonDecreasing(PlatformData platform, bool durable)
    {
        double? previous = null;
        foreach (int count in CardinalityCounts)
        {
            var rows = platform.Cardinality.Where(row => IsMeasured(row)
                && ParseBoolean(row["durable"]) == durable && ParseInt(row["object_count"]) == count).ToArray();
            if (rows.Length == 0)
                return false;
            double median = Statistics.Summarise(rows.Select(row => ParseDouble(row["file_persist_requests"]) ?? 0)).Median ?? 0;
            if (previous is not null && median < previous.Value)
                return false;
            previous = median;
        }
        return true;
    }

    private static int CountCompletedProductionCells(PlatformData platform)
        => (from representation in Representations
            from durable in DurabilityLevels
            from lifecycle in Lifecycles
            let rows = platform.Production.Where(row => IsMeasured(row) && row["representation"] == representation
                && row["lifecycle"] == lifecycle && ParseBoolean(row["durable"]) == durable)
            where rows.Count() == 15
            select 1).Count();

    private static int CountCompletedCardinalityCells(PlatformData platform)
        => (from count in CardinalityCounts
            from durable in DurabilityLevels
            let rows = platform.Cardinality.Where(row => IsMeasured(row)
                && ParseInt(row["object_count"]) == count && ParseBoolean(row["durable"]) == durable)
            where rows.Count() == 15
            select 1).Count();

    private static int CountCorrectnessFailures(IEnumerable<PlatformData> platforms)
        => platforms.Sum(platform => platform.Production.Count(static row => !string.IsNullOrEmpty(row["error"]))
            + platform.Cardinality.Count(static row => !string.IsNullOrEmpty(row["error"]))
            + platform.Recovery.Count(static row => row["termination_kind"] != "not_applicable" && !string.IsNullOrEmpty(row["error"])));

    private static int CountRecoveryViolations(IEnumerable<PlatformData> platforms)
        => platforms.Sum(platform => platform.Recovery.Count(row => row["termination_kind"] != "not_applicable"
            && (row["recovered_generation_class"] is "invalid_partial" or "unopenable"
                || ParseLong(row["missing_file_count"]) > 0
                || ParseLong(row["truncated_file_count"]) > 0
                || row["checkpoint"] == "after_commit_return"
                    && (row["recovered_generation_class"] != "new_complete" || ParseLong(row["tmp_file_count"]) != 0))));

    private static void AppendClassification(
        StringBuilder output,
        string classification,
        bool proceed,
        IReadOnlyDictionary<string, PlatformQualification> qualifications,
        IReadOnlyList<PlatformData> platforms,
        IReadOnlyDictionary<int, string> launchSensitivity,
        bool sensitivityChanged)
    {
        output.AppendLine("## Classification");
        output.AppendLine();
        output.AppendLine($"Classification: `{classification}`");
        output.AppendLine($"Proceed to main study: `{(proceed ? "yes" : "no")}`");
        output.AppendLine();
        output.AppendLine("| Platform | Evidence status | Recovery pass | Durable cardinality Spearman | Criteria 1-4 | End-to-end operation criterion | Qualifies |");
        output.AppendLine("|---|---|---|---:|---|---|---|");
        foreach (PlatformData platform in platforms)
        {
            PlatformQualification q = qualifications[platform.PlatformId];
            output.AppendLine($"| {platform.PlatformId} | {(platform.HasEnvironment ? (q.Valid ? "complete" : "incomplete") : "missing")} | {q.RecoveryPass} | {F(q.Spearman)} | {q.PremiseCriteriaPass} | {q.OperationCriterionPass} | {q.PremiseSupported} |");
        }
        output.AppendLine();
        output.AppendLine("## Leave-one-launch sensitivity");
        output.AppendLine();
        output.AppendLine("| Omitted launch | Recomputed classification |");
        output.AppendLine("|---:|---|");
        foreach (var (launch, result) in launchSensitivity)
            output.AppendLine($"| {launch} | {result} |");
        if (launchSensitivity.Count == 0)
            output.AppendLine("| n/a | Not evaluated because recovery correctness already failed. |");
        output.AppendLine($"Classification changed after removing one launch: {sensitivityChanged.ToString().ToLowerInvariant()}");
        output.AppendLine();
        output.AppendLine("## Final result record");
        output.AppendLine();
        output.AppendLine("```text");
        output.AppendLine($"Selected vnext base SHA: {ReadFirst(platforms, "base-sha.txt")}");
        output.AppendLine($"Spike branch SHA: {ReadFirst(platforms, "spike-sha.txt")}");
        PlatformData windows = platforms.Single(static platform => platform.PlatformId == "windows-ntfs");
        PlatformData linux = platforms.Single(static platform => platform.PlatformId == "linux-ext4");
        output.AppendLine($"Windows environment ID: {(windows.EnvironmentId.Length > 0 ? windows.EnvironmentId : "missing")}");
        output.AppendLine($"Linux environment ID: {(linux.EnvironmentId.Length > 0 ? linux.EnvironmentId : "missing")}");
        output.AppendLine($"Corpus SHA-256: {ReadCorpusHash(platforms)}");
        output.AppendLine($"Production cells completed / expected: {platforms.Sum(CountCompletedProductionCells)} / 16");
        output.AppendLine($"Cardinality cells completed / expected: {platforms.Sum(CountCompletedCardinalityCells)} / 28");
        output.AppendLine($"Recovery trials completed / expected: {platforms.Sum(static platform => platform.Recovery.Count(static row => row["termination_kind"] != "not_applicable"))} / 90");
        output.AppendLine("Loose -> compound persistence-request change: see Table B");
        output.AppendLine("Loose -> compound durability-time change: see Table B");
        output.AppendLine("Median DiD by platform/lifecycle: see Table B");
        output.AppendLine("Median net operation saving by platform/lifecycle: see Table B");
        output.AppendLine("Cardinality Spearman correlation by platform: see Table D and dose-response statistics");
        output.AppendLine($"Recovery violations: {CountRecoveryViolations(platforms)}");
        output.AppendLine($"Classification: {classification}");
        output.AppendLine($"Proceed to main study: {(proceed ? "yes" : "no")}");
        output.AppendLine($"Reason: {BuildRationale(classification, qualifications, platforms.Any(static platform => !platform.HasEnvironment), CountCorrectnessFailures(platforms), sensitivityChanged)}");
        output.AppendLine($"Evidence root: {Path.GetFullPath(Path.Combine(platforms[0].Root, ".."))}");
        output.AppendLine("Evidence SHA-256 manifest: sha256sums.txt");
        output.AppendLine("```");
        output.AppendLine();
        foreach (PlatformData platform in platforms.Where(static platform => !platform.HasEnvironment))
        {
            output.AppendLine($"Missing platform: `{platform.PlatformId}`. {platform.MissingReason}");
            output.AppendLine();
        }
    }

    private static string BuildRationale(
        string classification,
        IReadOnlyDictionary<string, PlatformQualification> qualification,
        bool missingPlatform,
        int correctnessFailures,
        bool sensitivityChanged)
    {
        if (classification == "correctness_failure")
            return "At least one recovery trial exposed an invalid partial generation, a missing or truncated referenced file, or failed the post-return publication contract.";
        if (missingPlatform)
            return "At least one required primary platform is missing, so the cross-platform classification is inconclusive.";
        if (sensitivityChanged)
            return "The performance classification changed when one harness launch was omitted, so the result is inconclusive.";
        if (classification == "premise_supported")
            return "Both primary platforms passed recovery, durability-time, persistence-request, cardinality, and end-to-end operation criteria in fresh and reopened lifecycles.";
        if (classification == "platform_specific")
            return "Exactly one complete primary platform passed all premise criteria.";
        if (classification == "mechanism_present_but_packing_cancels")
            return "At least one platform passed recovery, direct durability, persistence-request, and cardinality criteria, but compound operation time exceeded loose mode by more than five percent.";
        if (classification == "cardinality_not_explanatory")
            return "Compound mode reduced durability time materially, but the durable synthetic cardinality sweep did not support the predeclared dose-response threshold.";
        if (classification == "does_not_reproduce")
            return "Neither platform showed both the required compound durability-time reduction and a lower persistence-request count.";
        return $"The predeclared experiment is incomplete or has {correctnessFailures} recorded correctness or harness failures.";
    }

    private static bool ProductionCorrect(IReadOnlyDictionary<string, string> row)
        => string.IsNullOrEmpty(row["error"]) && row["reopen_ok"] == "true"
            && row["id_lookup_pass"] == "true" && row["index_integrity_pass"] == "true";

    private static bool IsMeasured(IReadOnlyDictionary<string, string> row)
        => row.TryGetValue("warmup", out string? value) && value == "false";

    private static bool ParseBoolean(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static int ParseInt(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;

    private static long ParseLong(string value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0;

    private static double? ParseDouble(string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : null;

    private static string F(double? value)
        => value is null || !double.IsFinite(value.Value) ? "n/a" : value.Value.ToString("0.###", CultureInfo.InvariantCulture);

    private static StatSummary ProductionMetric(
        PlatformData platform,
        string lifecycle,
        string representation,
        bool durable,
        string metric,
        Random? bootstrap = null)
        => Statistics.Summarise(platform.Production.Where(row => IsMeasured(row)
                && row["lifecycle"] == lifecycle && row["representation"] == representation
                && ParseBoolean(row["durable"]) == durable)
            .Select(row => ParseDouble(row[metric]))
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value), bootstrap);

    private static string ReadFirst(IReadOnlyList<PlatformData> platforms, string fileName)
        => platforms.Select(platform => Path.Combine(platform.Root, fileName))
            .Where(File.Exists)
            .Select(static path => File.ReadAllText(path).Trim())
            .FirstOrDefault(static value => value.Length > 0) ?? string.Empty;

    private static string ReadCorpusHash(IReadOnlyList<PlatformData> platforms)
    {
        foreach (PlatformData platform in platforms)
        {
            string path = Path.Combine(platform.Root, "dataset-identity.json");
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                return document.RootElement.GetProperty("contentSha256").GetString() ?? string.Empty;
            }
        }
        return string.Empty;
    }

    private static void WriteSha256Manifest(string aggregateRoot, IReadOnlyList<PlatformData> platforms)
    {
        string outputPath = Path.Combine(aggregateRoot, "sha256sums.txt");
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(aggregateRoot, "*", SearchOption.TopDirectoryOnly))
            if (IsManifestFile(file))
                candidates.Add(file);
        foreach (PlatformData platform in platforms)
        {
            if (Directory.Exists(platform.Root))
            {
                foreach (string file in Directory.EnumerateFiles(platform.Root, "*", SearchOption.AllDirectories))
                    if (IsManifestFile(file))
                        candidates.Add(file);
                string datasetDirectory = Path.Combine(platform.Root, "dataset");
                foreach (string name in new[]
                         {
                             "records.ndjson",
                             "record-offsets.bin",
                             "../cardinality-payload.bin",
                             "../cardinality-payload.sha256"
                         })
                {
                    string file = Path.GetFullPath(Path.Combine(datasetDirectory, name));
                    if (File.Exists(file))
                        candidates.Add(file);
                }
                foreach (string file in Directory.Exists(Path.Combine(platform.Root, "logs"))
                             ? Directory.EnumerateFiles(Path.Combine(platform.Root, "logs"), "*", SearchOption.AllDirectories)
                             : [])
                    candidates.Add(file);
            }
        }

        using var writer = new StreamWriter(outputPath, append: false, new UTF8Encoding(false));
        foreach (string file in candidates.Where(file => !Path.GetFullPath(file).Equals(outputPath, StringComparison.Ordinal))
                     .OrderBy(static file => file, StringComparer.Ordinal))
            writer.WriteLine($"{HashFile(file)}  {Path.GetRelativePath(aggregateRoot, file).Replace('\\', '/')}");
    }

    private static bool IsManifestFile(string path)
    {
        string extension = Path.GetExtension(path);
        return extension is ".csv" or ".json" or ".md" or ".txt" or ".svg" or ".log";
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void WriteDoseResponseSvg(string aggregateRoot, IReadOnlyList<PlatformData> platforms, Random bootstrap)
    {
        int panelHeight = 300;
        const int width = 780;
        int height = 80 + platforms.Count * panelHeight;
        var svg = new StringBuilder();
        svg.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">");
        svg.AppendLine("<style>text{font:13px sans-serif;fill:#222}.axis{stroke:#333;stroke-width:1}.grid{stroke:#ddd;stroke-width:1}.loose{stroke:#1769aa;fill:#1769aa}.compound{stroke:#bd3b27;fill:#bd3b27}.line{fill:none;stroke-width:2}</style>");
        svg.AppendLine("<text x=\"24\" y=\"28\" font-size=\"18\" font-weight=\"bold\">Synthetic persistence cardinality dose response</text>");
        for (int panel = 0; panel < platforms.Count; panel++)
        {
            PlatformData platform = platforms[panel];
            int top = 52 + panel * panelHeight;
            const int left = 88;
            const int right = 728;
            int bottom = top + 218;
            double maximum = CardinalityCounts.SelectMany(count => DurabilityLevels.Select(durable =>
                Statistics.Summarise(platform.Cardinality.Where(row => IsMeasured(row)
                    && ParseInt(row["object_count"]) == count && ParseBoolean(row["durable"]) == durable)
                    .Select(row => ParseDouble(row["durability_sync_ms"]) ?? 0), bootstrap).CiHigh ?? 0))
                .DefaultIfEmpty(1).Max();
            maximum = Math.Max(maximum, 1) * 1.1;
            svg.AppendLine($"<text x=\"{left}\" y=\"{top - 8}\" font-weight=\"bold\">{Escape(platform.PlatformId)}</text>");
            for (int tick = 0; tick <= 4; tick++)
            {
                double y = bottom - (bottom - top) * tick / 4d;
                double value = maximum * tick / 4d;
                svg.AppendLine($"<line class=\"grid\" x1=\"{left}\" y1=\"{y:F1}\" x2=\"{right}\" y2=\"{y:F1}\"/>");
                svg.AppendLine($"<text x=\"{left - 12}\" y=\"{y + 4:F1}\" text-anchor=\"end\">{value:F1}</text>");
            }
            svg.AppendLine($"<line class=\"axis\" x1=\"{left}\" y1=\"{top}\" x2=\"{left}\" y2=\"{bottom}\"/>");
            svg.AppendLine($"<line class=\"axis\" x1=\"{left}\" y1=\"{bottom}\" x2=\"{right}\" y2=\"{bottom}\"/>");
            svg.AppendLine($"<text transform=\"translate(20 {top + 120}) rotate(-90)\" text-anchor=\"middle\">durability_sync_ms</text>");
            for (int index = 0; index < CardinalityCounts.Length; index++)
            {
                double x = left + (right - left) * index / (CardinalityCounts.Length - 1d);
                svg.AppendLine($"<text x=\"{x:F1}\" y=\"{bottom + 21}\" text-anchor=\"middle\">{CardinalityCounts[index]}</text>");
            }
            svg.AppendLine($"<text x=\"{(left + right) / 2}\" y=\"{bottom + 43}\" text-anchor=\"middle\">payload object count (log2 scale)</text>");

            foreach (bool durable in DurabilityLevels)
            {
                string css = durable ? "compound" : "loose";
                var points = new List<(double X, double Y, double Low, double High)>();
                for (int index = 0; index < CardinalityCounts.Length; index++)
                {
                    int count = CardinalityCounts[index];
                    var rows = platform.Cardinality.Where(row => IsMeasured(row)
                        && ParseInt(row["object_count"]) == count && ParseBoolean(row["durable"]) == durable);
                    StatSummary stats = Statistics.Summarise(rows.Select(row => ParseDouble(row["durability_sync_ms"]) ?? 0), bootstrap);
                    if (stats.N == 0)
                        continue;
                    double x = left + (right - left) * index / (CardinalityCounts.Length - 1d);
                    double median = stats.Median ?? 0;
                    double y = bottom - (bottom - top) * median / maximum;
                    double low = bottom - (bottom - top) * (stats.CiLow ?? median) / maximum;
                    double high = bottom - (bottom - top) * (stats.CiHigh ?? median) / maximum;
                    points.Add((x, y, low, high));
                }
                if (points.Count == 0)
                    continue;
                string polyline = string.Join(' ', points.Select(static point => $"{point.X:F1},{point.Y:F1}"));
                svg.AppendLine($"<polyline class=\"line {css}\" points=\"{polyline}\"/>");
                foreach (var point in points)
                {
                    svg.AppendLine($"<line class=\"{css}\" x1=\"{point.X:F1}\" y1=\"{point.Low:F1}\" x2=\"{point.X:F1}\" y2=\"{point.High:F1}\"/>");
                    svg.AppendLine($"<circle class=\"{css}\" cx=\"{point.X:F1}\" cy=\"{point.Y:F1}\" r=\"4\"/>");
                }
                int legendY = top + 16 + (durable ? 18 : 0);
                svg.AppendLine($"<line class=\"{css}\" x1=\"{right - 105}\" y1=\"{legendY - 4}\" x2=\"{right - 80}\" y2=\"{legendY - 4}\"/>");
                svg.AppendLine($"<text x=\"{right - 74}\" y=\"{legendY}\">durable={durable}</text>");
            }
        }
        svg.AppendLine("</svg>");
        File.WriteAllText(Path.Combine(aggregateRoot, "cardinality-dose-response.svg"), svg.ToString(), new UTF8Encoding(false));
    }

    private static string Escape(string value)
        => value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    private sealed record PlatformData(
        string PlatformId,
        string Root,
        bool HasEnvironment,
        string EnvironmentId,
        string MissingReason,
        List<Dictionary<string, string>> Production,
        List<Dictionary<string, string>> Cardinality,
        List<Dictionary<string, string>> Recovery);

    private sealed record PlatformQualification(
        bool Valid,
        bool RecoveryPass,
        double Spearman,
        bool PremiseCriteriaPass,
        bool OperationCriterionPass,
        bool PremiseSupported,
        bool DurabilityAndRequestsPass,
        bool RequestDoseResponsePass);

    private sealed record MatchedComparison(
        StatSummary LoosePenalty,
        StatSummary CompoundPenalty,
        StatSummary DifferenceInDifferences,
        StatSummary DurabilitySaving,
        StatSummary DurabilitySavingPercent,
        StatSummary OperationSaving);

    private readonly record struct StatSummary(
        int N,
        double? Median,
        double? Q1,
        double? Q3,
        double? Minimum,
        double? Maximum,
        double? CiLow,
        double? CiHigh)
    {
        public double? Iqr => Q1 is null || Q3 is null ? null : Q3 - Q1;
        public string CiText => CiLow is null || CiHigh is null ? "n/a" : $"[{F(CiLow)}, {F(CiHigh)}]";
    }

    private static class Statistics
    {
        public static StatSummary Summarise(IEnumerable<double> values, Random? bootstrap = null)
        {
            double[] sorted = values.Where(double.IsFinite).Order().ToArray();
            if (sorted.Length == 0)
                return new StatSummary(0, null, null, null, null, null, null, null);
            double median = Quantile(sorted, 0.5);
            double ciLow = median;
            double ciHigh = median;
            if (sorted.Length > 1 && bootstrap is not null)
            {
                double[] medians = new double[10_000];
                var sample = new double[sorted.Length];
                for (int iteration = 0; iteration < medians.Length; iteration++)
                {
                    for (int index = 0; index < sample.Length; index++)
                        sample[index] = sorted[bootstrap.Next(sorted.Length)];
                    Array.Sort(sample);
                    medians[iteration] = Quantile(sample, 0.5);
                }
                Array.Sort(medians);
                ciLow = Quantile(medians, 0.025);
                ciHigh = Quantile(medians, 0.975);
            }
            return new StatSummary(sorted.Length, median, Quantile(sorted, 0.25), Quantile(sorted, 0.75),
                sorted[0], sorted[^1], ciLow, ciHigh);
        }

        public static double Spearman(IReadOnlyList<double> x, IReadOnlyList<double> y)
        {
            if (x.Count != y.Count || x.Count < 2)
                return double.NaN;
            return Pearson(Rank(x), Rank(y));
        }

        public static double Slope(IReadOnlyList<double> x, IReadOnlyList<double> y)
        {
            if (x.Count != y.Count || x.Count < 2)
                return double.NaN;
            double meanX = x.Average();
            double meanY = y.Average();
            double covariance = 0;
            double variance = 0;
            for (int index = 0; index < x.Count; index++)
            {
                double deltaX = x[index] - meanX;
                covariance += deltaX * (y[index] - meanY);
                variance += deltaX * deltaX;
            }
            return variance == 0 ? double.NaN : covariance / variance;
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
            return varianceX == 0 || varianceY == 0 ? double.NaN : numerator / Math.Sqrt(varianceX * varianceY);
        }

        private static double Quantile(IReadOnlyList<double> sorted, double probability)
        {
            if (sorted.Count == 1)
                return sorted[0];
            double position = (sorted.Count - 1) * probability;
            int lower = (int)Math.Floor(position);
            int upper = (int)Math.Ceiling(position);
            if (lower == upper)
                return sorted[lower];
            double fraction = position - lower;
            return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
        }
    }
}
