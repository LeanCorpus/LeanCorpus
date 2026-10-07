using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rowles.LeanCorpus.WindowsDurabilitySpike.Analysis;

internal static class MechanismAnalysis
{
    private static readonly int[] LocalCounts = [1, 4, 16, 64, 128];
    private static readonly int[] HostedCounts = [1, 16, 64, 128];

    internal static int Run(SpikeArguments arguments)
    {
        string input = Path.GetFullPath(arguments.Required("input"));
        string output = Path.GetFullPath(arguments.Required("output"));
        List<MechanismSample> samples = ReadSamples(input);
        if (samples.Count == 0)
            throw new InvalidDataException($"No windows-file-mechanism.csv files were found under '{input}'.");
        string[] environments = samples.Select(static sample => sample.Environment)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (environments.Length != 1)
            throw new InvalidDataException("Analyse one environment at a time; local VM and hosted samples must remain separate.");
        string environment = environments[0];
        bool hosted = environment.StartsWith("hosted_", StringComparison.Ordinal);
        bool ubuntuControl = environment == "hosted_ubuntu_24_04";
        if (environment != "local_windows_vm" && environment != "hosted_windows_2025" && !ubuntuControl)
            throw new InvalidDataException($"Unsupported mechanism environment '{environment}'.");
        int defaultLaunches = hosted ? 3 : 5;
        int defaultMeasuredPerLaunch = hosted ? 3 : 5;
        int expectedLaunches = int.Parse(arguments.Optional("expected-launches", defaultLaunches.ToString(
                System.Globalization.CultureInfo.InvariantCulture)),
            System.Globalization.CultureInfo.InvariantCulture);
        int expectedPerLaunch = int.Parse(arguments.Optional("measured-per-launch", defaultMeasuredPerLaunch.ToString(
                System.Globalization.CultureInfo.InvariantCulture)),
            System.Globalization.CultureInfo.InvariantCulture);
        if (expectedLaunches != defaultLaunches || expectedPerLaunch != defaultMeasuredPerLaunch)
            throw new ArgumentException($"Environment {environment} requires {defaultLaunches} launches and {defaultMeasuredPerLaunch} measured observations per launch.");
        int[] counts = hosted ? HostedCounts : LocalCounts;
        string[] variants = ubuntuControl
            ? ["D_leancorpus_wrapper"]
            : ["A_current_full", "B_open_close_only", "C_flush_only_preopened", "D_leancorpus_wrapper"];

        List<MechanismFileSample> fileSamples = ReadFileSamples(input);
        var measured = samples.Where(static sample => !sample.WarmUp).ToArray();
        var successful = measured.Where(static sample => sample.Success).ToArray();
        var attemptsByCell = measured.GroupBy(static sample => sample.CellKey)
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        var summaries = new List<CellSummary>();
        foreach (var group in successful.GroupBy(static sample => sample.CellKey).OrderBy(static group => group.Key))
        {
            double[] values = group.Select(static sample => sample.TotalMs).ToArray();
            (double lower, double upper) = MechanismStatistics.BootstrapMedian95(values, SeedFor(group.Key));
            MechanismSample first = group.First();
            int attempts = attemptsByCell.TryGetValue(group.Key, out MechanismSample[]? rows) ? rows.Length : 0;
            summaries.Add(new CellSummary(
                first.PayloadBytes, first.FileCount, first.Variant,
                values.Length, attempts, attempts - values.Length,
                MechanismStatistics.Median(values),
                MechanismStatistics.Percentile(values, 0.25),
                MechanismStatistics.Percentile(values, 0.75), lower, upper,
                MechanismStatistics.Median(group.Select(static sample => sample.WriteToSyncGapMs).ToArray())));
        }

        var phaseSummaries = SummarisePhases(successful, fileSamples);
        var phaseShares = SummarisePhaseShares(successful, fileSamples);
        var launchMedians = SummariseLaunchMedians(successful);
        var leaveOneOut = SummariseLeaveOneLaunchOut(successful, counts);
        var correlations = SummariseCardinality(successful, counts);
        var matched = SummariseMatchedWrapper(successful, fileSamples, counts);
        var gapSummaries = SummariseWriteToSyncGaps(measured, variants);
        var unstableGapCells = gapSummaries.Where(static row => !row.Stable).ToArray();
        var cleanComparisons = SummariseCleanVsDirty(successful, fileSamples, counts);
        var cleanEffect = ClassifyCleanBarrier(successful, counts, expectedLaunches, expectedPerLaunch);
        string classification = ubuntuControl ? "not_applicable_control_only" :
            ClassifyPrimary(successful, fileSamples, counts, expectedLaunches);
        string[][] inversions = FindInversions(summaries, counts);
        int failedCount = measured.Count(static sample => !sample.Success);
        int expectedRows = CalculateExpectedRows(counts.Length, variants.Length, expectedLaunches, expectedPerLaunch, !hosted);
        bool complete = measured.Length == expectedRows && failedCount == 0 &&
                        HasExpectedMatrix(samples, counts, variants, expectedLaunches, expectedPerLaunch, !hosted);
        bool environmentStable = unstableGapCells.Length == 0;
        if (!complete || !environmentStable)
            classification = "unstable_or_inconclusive";

        string analysisDirectory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(analysisDirectory);
        var report = new
        {
            schema_version = 1,
            input_directory = input,
            expected_launches = expectedLaunches,
            expected_measured_per_launch = expectedPerLaunch,
            measured_attempts = measured.Length,
            successful_measured_attempts = successful.Length,
            failed_measured_attempts = failedCount,
            warm_up_attempts_excluded = samples.Count(static sample => sample.WarmUp),
            matrix_complete = complete,
            environment_stable = environmentStable,
            primary_classification = classification,
            clean_barrier_effect = cleanEffect,
            cells = summaries,
            phase_summaries = phaseSummaries,
            phase_shares = phaseShares,
            launch_level_medians = launchMedians,
            matched_d_minus_a = matched,
            cardinality_spearman = correlations,
            leave_one_launch_out = leaveOneOut,
            write_to_sync_gap_distributions = gapSummaries,
            clean_reflush_vs_dirty_flush = cleanComparisons,
            inversions = inversions,
            analysis_rules = new
            {
                confidence_interval = "10,000 deterministic bootstrap resamples of the median; percentile 2.5 and 97.5 bounds",
                failed_attempts = "retained in raw input, counted as failures, excluded only from latency estimates; no replacement samples",
                primary_classification = "mechanical Spike 2 thresholds; hosted four-count matrices use the same 80 percent cell coverage rule rounded up; incomplete matrix, failed measured attempts, or unstable write-to-sync gaps yield unstable_or_inconclusive",
                write_to_sync_gap_stability = "within each payload/count block, compare each primary variant median with the median of variant medians; unstable when the absolute difference exceeds max(5 ms, three times the median within-variant IQR)",
                clean_barrier_effect = "present only when E endpoint medians increase for both payloads and all three E-launch Spearman correlations are positive; otherwise weak for partial support, absent for no positive direction, inconclusive for incomplete data"
            }
        };
        File.WriteAllText(Path.ChangeExtension(output, ".json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        File.WriteAllText(output, FormatMarkdown(report, summaries, phaseSummaries, phaseShares,
            launchMedians, matched, correlations, leaveOneOut, gapSummaries, cleanComparisons, inversions), new UTF8Encoding(false));
        Console.WriteLine($"Analysed {measured.Length} measured attempts: {classification}; clean barrier {cleanEffect}.");
        return complete ? 0 : 1;
    }

    private static List<MechanismSample> ReadSamples(string input)
    {
        var result = new List<MechanismSample>();
        foreach (string path in Directory.EnumerateFiles(input, "windows-file-mechanism.csv", SearchOption.AllDirectories))
        foreach (string[] row in CsvReader.Read(path))
        {
            if (row.Length != 23)
                throw new InvalidDataException($"Malformed mechanism row in '{path}'.");
            result.Add(new MechanismSample(
                row[0], int.Parse(row[1]), long.Parse(row[5]), int.Parse(row[6]), row[7],
                int.Parse(row[8]), bool.Parse(row[9]), bool.Parse(row[10]),
                ParseDouble(row[11]), ParseDouble(row[12]), ParseDouble(row[13]),
                ParseDouble(row[14]), ParseDouble(row[15]), row[19]));
        }
        return result;
    }

    private static List<MechanismFileSample> ReadFileSamples(string input)
    {
        var result = new List<MechanismFileSample>();
        foreach (string path in Directory.EnumerateFiles(input, "windows-file-mechanism-per-file.csv", SearchOption.AllDirectories))
        foreach (string[] row in CsvReader.Read(path))
        {
            if (row.Length != 21)
                throw new InvalidDataException($"Malformed per-file mechanism row in '{path}'.");
            result.Add(new MechanismFileSample(
                row[0], int.Parse(row[1]), long.Parse(row[2]), int.Parse(row[3]), row[4],
                int.Parse(row[5]), bool.Parse(row[6]), int.Parse(row[7]), long.Parse(row[8]),
                ParseDouble(row[9]), ParseDouble(row[10]), ParseDouble(row[11]), ParseDouble(row[12]),
                bool.Parse(row[13]), bool.Parse(row[14]), bool.Parse(row[15]), bool.Parse(row[16]),
                int.Parse(row[17]), ParseDouble(row[18]), int.Parse(row[19]), ParseDouble(row[20])));
        }
        return result;
    }

    private static object[] SummarisePhases(
        IReadOnlyList<MechanismSample> successful,
        IReadOnlyList<MechanismFileSample> files)
    {
        var measurements = GetPhaseMeasurements(successful, files);
        return measurements.Where(static item => item.Variant is "A_current_full" or "B_open_close_only" or "C_flush_only_preopened")
            .GroupBy(static item => (item.PayloadBytes, item.FileCount, item.Variant))
            .OrderBy(static group => group.Key.PayloadBytes).ThenBy(static group => group.Key.FileCount)
            .ThenBy(static group => group.Key.Variant, StringComparer.Ordinal)
            .Select(group => new
            {
                payload_bytes = group.Key.PayloadBytes,
                file_count = group.Key.FileCount,
                variant = group.Key.Variant,
                median_open_ms_per_file = MechanismStatistics.Median(group.Select(static value => value.OpenPerFileMs).ToArray()),
                median_flush_ms_per_file = MechanismStatistics.Median(group.Select(static value => value.FlushPerFileMs).ToArray()),
                median_close_ms_per_file = MechanismStatistics.Median(group.Select(static value => value.ClosePerFileMs).ToArray()),
                median_retry_delay_ms = MechanismStatistics.Median(group.Select(static value => value.RetryDelayMs).ToArray())
            }).Cast<object>().ToArray();
    }

    private static object[] SummarisePhaseShares(
        IReadOnlyList<MechanismSample> successful,
        IReadOnlyList<MechanismFileSample> files)
    {
        return GetPhaseMeasurements(successful, files)
            .Where(static row => row.Variant == "A_current_full")
            .GroupBy(static row => (row.PayloadBytes, row.FileCount))
            .OrderBy(static group => group.Key.PayloadBytes).ThenBy(static group => group.Key.FileCount)
            .Select(group => new
            {
                payload_bytes = group.Key.PayloadBytes,
                file_count = group.Key.FileCount,
                successful_observations = group.Count(),
                median_flush_share = MechanismStatistics.Median(group.Select(static row =>
                    row.FlushMs / Math.Max(row.TotalMs, double.Epsilon)).ToArray()),
                median_open_close_share = MechanismStatistics.Median(group.Select(static row =>
                    (row.OpenMs + row.CloseMs) / Math.Max(row.TotalMs, double.Epsilon)).ToArray())
            }).Cast<object>().ToArray();
    }

    private static object[] SummariseLaunchMedians(IReadOnlyList<MechanismSample> successful)
    {
        return successful.GroupBy(static row => (row.Environment, row.Launch, row.PayloadBytes, row.FileCount, row.Variant))
            .OrderBy(static group => group.Key.Environment, StringComparer.Ordinal)
            .ThenBy(static group => group.Key.Launch)
            .ThenBy(static group => group.Key.PayloadBytes)
            .ThenBy(static group => group.Key.FileCount)
            .ThenBy(static group => group.Key.Variant, StringComparer.Ordinal)
            .Select(group => new
            {
                environment = group.Key.Environment,
                launch = group.Key.Launch,
                payload_bytes = group.Key.PayloadBytes,
                file_count = group.Key.FileCount,
                variant = group.Key.Variant,
                successful_observations = group.Count(),
                median_ms = MechanismStatistics.Median(group.Select(static row => row.TotalMs).ToArray())
            }).Cast<object>().ToArray();
    }

    private static GapSummary[] SummariseWriteToSyncGaps(
        IReadOnlyList<MechanismSample> measured,
        IReadOnlyList<string> variants)
    {
        GapSummary[] raw = measured.Where(row => variants.Contains(row.Variant, StringComparer.Ordinal))
            .GroupBy(static row => (row.PayloadBytes, row.FileCount, row.Variant))
            .Select(group =>
            {
                double[] values = group.Select(static row => row.WriteToSyncGapMs).ToArray();
                return new GapSummary(group.Key.PayloadBytes, group.Key.FileCount, group.Key.Variant,
                    values.Length, MechanismStatistics.Median(values),
                    MechanismStatistics.Percentile(values, 0.25), MechanismStatistics.Percentile(values, 0.75),
                    0, 0, 0, true, "pending");
            }).ToArray();

        var result = new List<GapSummary>(raw.Length);
        foreach (var block in raw.GroupBy(static row => (row.PayloadBytes, row.FileCount)))
        {
            double referenceMedian = MechanismStatistics.Median(block.Select(static row => row.MedianMs).ToArray());
            double referenceIqr = MechanismStatistics.Median(block.Select(static row => row.UpperQuartileMs - row.LowerQuartileMs).ToArray());
            double allowedDifference = Math.Max(5d, 3d * referenceIqr);
            bool compareVariants = block.Count() > 1;
            foreach (GapSummary row in block)
            {
                double difference = Math.Abs(row.MedianMs - referenceMedian);
                result.Add(row with
                {
                    ReferenceMedianMs = compareVariants ? referenceMedian : row.MedianMs,
                    MedianDifferenceMs = compareVariants ? difference : 0,
                    AllowedDifferenceMs = compareVariants ? allowedDifference : 0,
                    Stable = !compareVariants || difference <= allowedDifference,
                    Comparison = compareVariants ? "variant_medians_within_threshold" : "single_variant_control"
                });
            }
        }
        return result.OrderBy(static row => row.PayloadBytes).ThenBy(static row => row.FileCount)
            .ThenBy(static row => row.Variant, StringComparer.Ordinal).ToArray();
    }

    private static object[] SummariseCleanVsDirty(
        IReadOnlyList<MechanismSample> successful,
        IReadOnlyList<MechanismFileSample> files,
        IReadOnlyList<int> counts)
    {
        if (!successful.Any(static row => row.Variant == "E_clean_reflush_preopened"))
            return [];
        var output = new List<object>();
        foreach (long payload in successful.Select(static row => row.PayloadBytes).Distinct().Order())
        foreach (int count in counts)
        {
            double[] clean = successful.Where(row => row.PayloadBytes == payload && row.FileCount == count && row.Variant == "E_clean_reflush_preopened")
                .Select(static row => row.CleanReflushMs).ToArray();
            double[] preflush = successful.Where(row => row.PayloadBytes == payload && row.FileCount == count && row.Variant == "E_clean_reflush_preopened")
                .Select(static row => row.PreflushMs).ToArray();
            double[] dirtyAFlush = PerObservationFlush(files, payload, count, "A_current_full");
            double[] dirtyCFlush = successful.Where(row => row.PayloadBytes == payload && row.FileCount == count && row.Variant == "C_flush_only_preopened")
                .Select(static row => row.TotalMs).ToArray();
            double[] dirtyAFull = successful.Where(row => row.PayloadBytes == payload && row.FileCount == count && row.Variant == "A_current_full")
                .Select(static row => row.TotalMs).ToArray();
            if (clean.Length == 0)
                continue;
            output.Add(new
            {
                payload_bytes = payload,
                file_count = count,
                clean_reflush_observations = clean.Length,
                median_preflush_ms = MechanismStatistics.Median(preflush),
                median_clean_reflush_ms = MechanismStatistics.Median(clean),
                median_clean_reflush_per_file_ms = MechanismStatistics.Median(successful
                    .Where(row => row.PayloadBytes == payload && row.FileCount == count && row.Variant == "E_clean_reflush_preopened")
                    .Select(static row => row.CleanReflushPerFileMs).ToArray()),
                median_a_dirty_flush_ms = MechanismStatistics.Median(dirtyAFlush),
                median_c_dirty_flush_ms = MechanismStatistics.Median(dirtyCFlush),
                median_a_current_full_ms = MechanismStatistics.Median(dirtyAFull)
            });
        }
        return output.ToArray();
    }

    private static double[] PerObservationFlush(
        IReadOnlyList<MechanismFileSample> files,
        long payload,
        int count,
        string variant)
    {
        return files.Where(row => !row.WarmUp && row.PayloadBytes == payload && row.FileCount == count && row.Variant == variant)
            .GroupBy(static row => row.SamplingKey, StringComparer.Ordinal)
            .Select(static group => group.Sum(static row => row.FlushMs)).ToArray();
    }

    private static object[] SummariseMatchedWrapper(
        IReadOnlyList<MechanismSample> successful,
        IReadOnlyList<MechanismFileSample> files,
        IReadOnlyList<int> counts)
    {
        var output = new List<object>();
        foreach (long payload in successful.Select(static row => row.PayloadBytes).Distinct().Order())
        foreach (int count in counts)
        {
            double[] a = successful.Where(row => row.PayloadBytes == payload && row.FileCount == count && row.Variant == "A_current_full")
                .Select(static row => row.TotalMs).ToArray();
            double[] d = successful.Where(row => row.PayloadBytes == payload && row.FileCount == count && row.Variant == "D_leancorpus_wrapper")
                .Select(static row => row.TotalMs).ToArray();
            double[] retries = files.Where(row => row.PayloadBytes == payload && row.FileCount == count &&
                row.Variant == "D_leancorpus_wrapper" && !row.WarmUp)
                .GroupBy(static row => (row.Launch, row.Observation))
                .Select(static group => group.Sum(static row => row.RetryDelayMs)).ToArray();
            if (a.Length == 0 || d.Length == 0)
                continue;
            double medianA = MechanismStatistics.Median(a);
            double medianD = MechanismStatistics.Median(d);
            output.Add(new
            {
                payload_bytes = payload,
                file_count = count,
                median_a_ms = medianA,
                median_d_ms = medianD,
                median_d_minus_a_ms = medianD - medianA,
                median_d_over_a = medianA == 0 ? double.NaN : medianD / medianA,
                median_retry_delay_ms = retries.Length == 0 ? 0 : MechanismStatistics.Median(retries),
                median_d_minus_retry_ms = medianD - (retries.Length == 0 ? 0 : MechanismStatistics.Median(retries))
            });
        }
        return output.ToArray();
    }

    private static object[] SummariseCardinality(IReadOnlyList<MechanismSample> successful, IReadOnlyList<int> counts)
    {
        var output = new List<object>();
        foreach (long payload in successful.Select(static row => row.PayloadBytes).Distinct().Order())
        foreach (string variant in new[] { "A_current_full", "C_flush_only_preopened", "D_leancorpus_wrapper" })
        {
            int[] usedCounts = counts.Where(count => successful.Any(row => row.PayloadBytes == payload && row.FileCount == count && row.Variant == variant)).ToArray();
            double[] medians = usedCounts.Select(count => MechanismStatistics.Median(successful
                .Where(row => row.PayloadBytes == payload && row.FileCount == count && row.Variant == variant)
                .Select(static row => row.TotalMs).ToArray())).ToArray();
            if (usedCounts.Length >= 2)
                output.Add(new { payload_bytes = payload, variant, file_counts = usedCounts, median_ms = medians,
                    spearman_file_count_median_ms = MechanismStatistics.Spearman(usedCounts.Select(static value => (double)value).ToArray(), medians) });
        }
        return output.ToArray();
    }

    private static object[] SummariseLeaveOneLaunchOut(IReadOnlyList<MechanismSample> successful, IReadOnlyList<int> counts)
    {
        var output = new List<object>();
        int[] launches = successful.Select(static sample => sample.Launch).Distinct().Order().ToArray();
        foreach (long payload in successful.Select(static row => row.PayloadBytes).Distinct().Order())
        foreach (int count in counts)
        foreach (string variant in new[] { "A_current_full", "C_flush_only_preopened", "D_leancorpus_wrapper", "E_clean_reflush_preopened" })
        {
            (int Launch, double Value)[] cell = successful.Where(row => row.PayloadBytes == payload && row.FileCount == count &&
                row.Variant == variant).Select(static row => (row.Launch, row.TotalMs)).ToArray();
            foreach (int omittedLaunch in launches)
            {
                (double median, int sampleCount) = MechanismStatistics.MedianOutsideLaunch(cell, omittedLaunch);
                if (sampleCount > 0)
                    output.Add(new { payload_bytes = payload, file_count = count, variant, omitted_launch = omittedLaunch,
                        median_ms = median, sample_count = sampleCount });
            }
        }
        return output.ToArray();
    }

    internal static (int MeasuredRows, int AttemptRows) ExpectedCountsFor(string environment)
    {
        int[] counts = environment == "local_windows_vm" ? LocalCounts : HostedCounts;
        bool local = environment == "local_windows_vm";
        bool ubuntu = environment == "hosted_ubuntu_24_04";
        int launches = local ? 5 : 3;
        int measuredPerLaunch = local ? 5 : 3;
        int variants = ubuntu ? 1 : 4;
        int measured = 2 * counts.Length * variants * launches * measuredPerLaunch;
        int attempts = 2 * counts.Length * variants * launches * (measuredPerLaunch + 1);
        if (local)
        {
            measured += 2 * counts.Length * 3 * 3;
            attempts += 2 * counts.Length * 3 * 4;
        }
        return (measured, attempts);
    }

    private static string ClassifyPrimary(
        IReadOnlyList<MechanismSample> successful,
        IReadOnlyList<MechanismFileSample> files,
        IReadOnlyList<int> counts,
        int expectedLaunches)
    {
        if (counts.Count < 2)
            return "unstable_or_inconclusive";
        int requiredCells = (int)Math.Ceiling(counts.Count * 0.8d);
        var phases = GetPhaseMeasurements(successful, files)
            .Where(static measurement => measurement.Variant == "A_current_full").ToArray();
        long[] payloads = successful.Select(static sample => sample.PayloadBytes).Distinct().Order().ToArray();
        if (payloads.Length != 2)
            return "unstable_or_inconclusive";

        bool IsDominated(bool flush)
        {
            int?[] omissions = [null, .. Enumerable.Range(1, expectedLaunches).Select(static launch => (int?)launch)];
            foreach (int? omitted in omissions)
            {
                foreach (long payload in payloads)
                {
                    var flushShares = new double[counts.Count];
                    var handleShares = new double[counts.Count];
                    for (int index = 0; index < counts.Count; index++)
                    {
                        int count = counts[index];
                        var cell = phases.Where(value => value.PayloadBytes == payload && value.FileCount == count &&
                            (omitted is null || value.Launch != omitted.Value)).ToArray();
                        flushShares[index] = MechanismStatistics.Median(cell.Select(value =>
                            value.FlushMs / Math.Max(value.TotalMs, double.Epsilon)).ToArray());
                        handleShares[index] = MechanismStatistics.Median(cell.Select(value =>
                            (value.OpenMs + value.CloseMs) / Math.Max(value.TotalMs, double.Epsilon)).ToArray());
                    }
                    string cellClassification = MechanismStatistics.ClassifyPhaseShares(flushShares, handleShares);
                    if (cellClassification != (flush ? "flush_dominated" : "handle_dominated"))
                        return false;
                }
            }
            return true;
        }

        if (IsDominated(flush: true))
            return "flush_dominated";
        if (IsDominated(flush: false))
            return "handle_dominated";

        bool mixedForBothPayloads = payloads.All(payload =>
        {
            var payloadPhases = phases.Where(value => value.PayloadBytes == payload).ToArray();
            if (payloadPhases.Length == 0)
                return false;
            double[] openClose = counts.Select(count => MechanismStatistics.Median(payloadPhases
                .Where(value => value.FileCount == count).Select(static value => value.OpenMs + value.CloseMs).ToArray())).ToArray();
            double[] flush = counts.Select(count => MechanismStatistics.Median(payloadPhases
                .Where(value => value.FileCount == count).Select(static value => value.FlushMs).ToArray())).ToArray();
            double[] sizes = counts.Select(static value => (double)value).ToArray();
            bool bothScale = openClose[^1] > openClose[0] && flush[^1] > flush[0] &&
                MechanismStatistics.Spearman(sizes, openClose) > 0 && MechanismStatistics.Spearman(sizes, flush) > 0;
            int explained = Enumerable.Range(0, counts.Count).Count(index =>
                openClose[index] + flush[index] >= 0.80 * MechanismStatistics.Median(payloadPhases
                    .Where(value => value.FileCount == counts[index]).Select(static value => value.TotalMs).ToArray()));
            return bothScale && explained >= requiredCells;
        });
        if (mixedForBothPayloads)
            return "mixed";

        bool wrapperDominated = payloads.All(payload =>
        {
            int qualifying = 0;
            foreach (int count in counts)
            {
                double[] a = successful.Where(row => row.PayloadBytes == payload && row.FileCount == count && row.Variant == "A_current_full")
                    .Select(static row => row.TotalMs).ToArray();
                double[] d = successful.Where(row => row.PayloadBytes == payload && row.FileCount == count && row.Variant == "D_leancorpus_wrapper")
                    .Select(static row => row.TotalMs).ToArray();
                double[] retry = files.Where(row => row.PayloadBytes == payload && row.FileCount == count &&
                    row.Variant == "D_leancorpus_wrapper" && !row.WarmUp)
                    .GroupBy(static row => (row.Launch, row.Observation)).Select(static group => group.Sum(static row => row.RetryDelayMs)).ToArray();
                if (a.Length == 0 || d.Length == 0)
                    continue;
                double netD = MechanismStatistics.Median(d) - (retry.Length == 0 ? 0 : MechanismStatistics.Median(retry));
                if (netD >= MechanismStatistics.Median(a) * 1.25)
                    qualifying++;
            }
            return qualifying >= requiredCells;
        });
        return wrapperDominated ? "wrapper_dominated" : "unstable_or_inconclusive";
    }

    private static string ClassifyCleanBarrier(
        IReadOnlyList<MechanismSample> successful,
        IReadOnlyList<int> counts,
        int expectedLaunches,
        int expectedPerLaunch)
    {
        if (expectedLaunches != 5 || expectedPerLaunch != 5 || counts.Count < 2)
            return "inconclusive";
        long[] payloads = successful.Select(static sample => sample.PayloadBytes).Distinct().Order().ToArray();
        if (payloads.Length != 2 || payloads.Any(payload => counts.Any(count => successful.Count(row =>
                row.PayloadBytes == payload && row.FileCount == count && row.Variant == "E_clean_reflush_preopened") != 9)))
            return "inconclusive";
        bool anyPositive = false;
        bool allPositive = payloads.Length == 2;
        foreach (long payload in payloads)
        {
            double[] overall = counts.Select(count => MechanismStatistics.Median(successful
                .Where(row => row.PayloadBytes == payload && row.FileCount == count && row.Variant == "E_clean_reflush_preopened")
                .Select(static row => row.CleanReflushMs).ToArray())).ToArray();
            bool increasing = overall.Length > 1 && overall[^1] > overall[0];
            anyPositive |= increasing;
            allPositive &= increasing;
            int[] launches = successful.Where(row => row.PayloadBytes == payload && row.Variant == "E_clean_reflush_preopened")
                .Select(static row => row.Launch).Distinct().Order().ToArray();
            allPositive &= launches.Length == 3 && launches.All(launch =>
            {
                double[] launchMedians = counts.Select(count => MechanismStatistics.Median(successful
                    .Where(row => row.Launch == launch && row.PayloadBytes == payload && row.FileCount == count &&
                                  row.Variant == "E_clean_reflush_preopened")
                    .Select(static row => row.CleanReflushMs).ToArray())).ToArray();
                return MechanismStatistics.Spearman(counts.Select(static value => (double)value).ToArray(), launchMedians) > 0;
            });
        }
        return allPositive ? "present" : anyPositive ? "weak" : "absent";
    }

    private static string[][] FindInversions(IReadOnlyList<CellSummary> summaries, IReadOnlyList<int> counts)
    {
        var result = new List<string[]>();
        foreach (long payload in summaries.Select(static summary => summary.PayloadBytes).Distinct().Order())
        foreach (string variant in new[] { "A_current_full", "C_flush_only_preopened", "D_leancorpus_wrapper", "E_clean_reflush_preopened" })
        {
            double? previous = null;
            int previousCount = 0;
            foreach (int count in counts)
            {
                CellSummary? cell = summaries.FirstOrDefault(summary => summary.PayloadBytes == payload &&
                    summary.FileCount == count && summary.Variant == variant);
                if (cell is null)
                    continue;
                double value = cell.MedianMs;
                if (previous is not null && value < previous.Value)
                    result.Add([payload.ToString(), variant, previousCount.ToString(), count.ToString(),
                        previous.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                        value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)]);
                previous = value;
                previousCount = count;
            }
        }
        return result.ToArray();

    }

    private static List<PhaseMeasurement> GetPhaseMeasurements(
        IReadOnlyList<MechanismSample> successful,
        IReadOnlyList<MechanismFileSample> fileSamples)
    {
        var sampleLookup = successful.ToDictionary(static sample => sample.SamplingKey, StringComparer.Ordinal);
        return fileSamples.Where(static file => !file.WarmUp)
            .Where(file => file.Variant is "A_current_full" or "C_flush_only_preopened")
            .GroupBy(static file => file.SamplingKey, StringComparer.Ordinal)
            .Where(group => sampleLookup.ContainsKey(group.Key))
            .Select(group =>
            {
                MechanismSample sample = sampleLookup[group.Key];
                return new PhaseMeasurement(sample.PayloadBytes, sample.FileCount, sample.Variant,
                    sample.Launch, sample.Observation,
                    group.Sum(static row => row.OpenMs), group.Sum(static row => row.FlushMs),
                    group.Sum(static row => row.CloseMs), sample.TotalMs,
                    group.Average(static row => row.OpenMs), group.Average(static row => row.FlushMs),
                    group.Average(static row => row.CloseMs), 0);
            })
            .Concat(fileSamples.Where(static file => !file.WarmUp && file.Variant == "D_leancorpus_wrapper")
                .GroupBy(static file => file.SamplingKey, StringComparer.Ordinal)
                .Where(group => sampleLookup.ContainsKey(group.Key))
                .Select(group =>
                {
                    MechanismSample sample = sampleLookup[group.Key];
                    double retry = group.Sum(static row => row.RetryDelayMs);
                    return new PhaseMeasurement(sample.PayloadBytes, sample.FileCount, sample.Variant,
                        sample.Launch, sample.Observation, 0, 0, 0, sample.TotalMs,
                        0, 0, 0, retry);
                })).ToList();
    }

    private static bool HasExpectedMatrix(
        IReadOnlyList<MechanismSample> allSamples,
        IReadOnlyList<int> counts,
        IReadOnlyList<string> primaryVariants,
        int launches,
        int measuredPerLaunch,
        bool includeClean)
    {
        long[] expectedPayloads = [8L * 1024 * 1024, 82L * 1024 * 1024];
        string[] variants = includeClean
            ? [.. primaryVariants, "E_clean_reflush_preopened"]
            : primaryVariants.ToArray();
        int expectedObservationCount = 2 * counts.Count * primaryVariants.Count * launches * (measuredPerLaunch + 1) +
            (includeClean ? 2 * counts.Count * 3 * 4 : 0);
        if (allSamples.Count != expectedObservationCount)
            return false;
        var expectedKeys = (from payload in expectedPayloads
                            from count in counts
                            from variant in variants
                            select MechanismSample.MakeCellKey(payload, count, variant))
            .ToHashSet(StringComparer.Ordinal);
        var actualKeys = allSamples.Select(static sample => sample.CellKey).ToHashSet(StringComparer.Ordinal);
        if (!expectedKeys.SetEquals(actualKeys))
            return false;

        foreach (long payload in expectedPayloads)
        foreach (int count in counts)
        foreach (string variant in primaryVariants)
        foreach (int launch in Enumerable.Range(1, launches))
        {
            MechanismSample[] rows = allSamples.Where(row => row.PayloadBytes == payload && row.FileCount == count &&
                row.Variant == variant && row.Launch == launch).ToArray();
            if (rows.Count(static row => row.WarmUp) != 1 ||
                rows.Count(static row => !row.WarmUp) != measuredPerLaunch)
                return false;
        }

        if (includeClean)
        foreach (long payload in expectedPayloads)
        foreach (int count in counts)
        foreach (int launch in Enumerable.Range(1, 3))
        {
            MechanismSample[] rows = allSamples.Where(row => row.PayloadBytes == payload && row.FileCount == count &&
                row.Variant == "E_clean_reflush_preopened" && row.Launch == launch).ToArray();
            if (rows.Count(static row => row.WarmUp) != 1 || rows.Count(static row => !row.WarmUp) != 3)
                return false;
        }
        return true;
    }

    private static int CalculateExpectedRows(
        int countCount,
        int primaryVariantCount,
        int launches,
        int measuredPerLaunch,
        bool includeClean)
    {
        int primary = 2 * countCount * primaryVariantCount * launches * measuredPerLaunch;
        int clean = includeClean ? 2 * countCount * 3 * 3 : 0;
        return primary + clean;
    }

    private static uint SeedFor(string key)
        => BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0);

    private static double ParseDouble(string value)
        => double.Parse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatMarkdown(
        object report,
        IReadOnlyList<CellSummary> cells,
        IReadOnlyList<object> phases,
        IReadOnlyList<object> phaseShares,
        IReadOnlyList<object> launchMedians,
        IReadOnlyList<object> matched,
        IReadOnlyList<object> correlations,
        IReadOnlyList<object> leaveOneOut,
        IReadOnlyList<GapSummary> gapSummaries,
        IReadOnlyList<object> cleanComparisons,
        IReadOnlyList<string[]> inversions)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# File durability mechanism summary");
        builder.AppendLine();
        builder.AppendLine($"Primary classification: **{report.GetType().GetProperty("primary_classification")?.GetValue(report)}**. Rows contain all measured attempts; warm-ups are excluded from estimates and failed attempts remain counted.");
        builder.AppendLine($"Clean barrier effect: **{report.GetType().GetProperty("clean_barrier_effect")?.GetValue(report)}**.");
        builder.AppendLine($"Environmentally stable write-to-sync gaps: **{report.GetType().GetProperty("environment_stable")?.GetValue(report)}**.");
        builder.AppendLine();
        builder.AppendLine("## Payload × file count × variant");
        builder.AppendLine();
        builder.AppendLine("| Payload bytes | Files | Variant | n | Failed | Median ms | IQR ms | Bootstrap 95% CI ms | Median write-to-sync gap ms |");
        builder.AppendLine("|---:|---:|---|---:|---:|---:|---:|---:|---:|");
        foreach (CellSummary cell in cells.OrderBy(static cell => cell.PayloadBytes).ThenBy(static cell => cell.FileCount).ThenBy(static cell => cell.Variant, StringComparer.Ordinal))
            builder.AppendLine($"| {cell.PayloadBytes} | {cell.FileCount} | {cell.Variant} | {cell.SuccessfulCount} | {cell.FailedCount} | {cell.MedianMs:F3} | {cell.UpperQuartileMs - cell.LowerQuartileMs:F3} | [{cell.CiLowerMs:F3}, {cell.CiUpperMs:F3}] | {cell.MedianWriteToSyncGapMs:F3} |");
        AppendJsonSection(builder, "Per-file phase medians", phases);
        AppendJsonSection(builder, "A current phase shares", phaseShares);
        AppendJsonSection(builder, "Launch-level medians", launchMedians);
        AppendJsonSection(builder, "Matched D minus A", matched);
        AppendJsonSection(builder, "File-count Spearman correlations", correlations);
        AppendJsonSection(builder, "Leave-one-launch-out medians", leaveOneOut);
        AppendJsonSection(builder, "Write-to-sync gap distributions and stability", gapSummaries.Cast<object>().ToArray());
        AppendJsonSection(builder, "Clean reflush versus dirty flush", cleanComparisons);
        builder.AppendLine("## Observed cardinality inversions");
        builder.AppendLine();
        if (inversions.Count == 0)
            builder.AppendLine("No decreasing adjacent medians were observed.");
        else
        {
            builder.AppendLine("| Payload bytes | Variant | From files | To files | From median ms | To median ms |");
            builder.AppendLine("|---:|---|---:|---:|---:|---:|");
            foreach (string[] row in inversions)
                builder.AppendLine($"| {string.Join(" | ", row)} |");
        }
        builder.AppendLine();
        builder.AppendLine("The JSON companion contains the full machine-readable tables and explicit sample-loss accounting.");
        return builder.ToString();
    }

    private static void AppendJsonSection(StringBuilder builder, string title, IReadOnlyList<object> values)
    {
        builder.AppendLine();
        builder.AppendLine("## " + title);
        builder.AppendLine();
        builder.AppendLine("```json");
        builder.AppendLine(JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
        builder.AppendLine("```");
    }

    private sealed record MechanismSample(
        string Environment,
        int Launch,
        long PayloadBytes,
        int FileCount,
        string Variant,
        int Observation,
        bool WarmUp,
        bool Success,
        double TotalMs,
        double WriteToSyncGapMs,
        double PreflushMs,
        double CleanReflushMs,
        double CleanReflushPerFileMs,
        string PayloadSha256)
    {
        internal string CellKey => MakeCellKey(PayloadBytes, FileCount, Variant);
        internal string SamplingKey => $"{Environment}|{Launch}|{PayloadBytes}|{FileCount}|{Variant}|{Observation}";
        internal static string MakeCellKey(long payload, int count, string variant) => $"{payload}|{count}|{variant}";
    }

    private sealed record MechanismFileSample(
        string Environment,
        int Launch,
        long PayloadBytes,
        int FileCount,
        string Variant,
        int Observation,
        bool WarmUp,
        int FileIndex,
        long FileBytes,
        double OpenMs,
        double FlushMs,
        double CloseMs,
        double FullMs,
        bool OpenSuccess,
        bool FlushAttempted,
        bool FlushSuccess,
        bool CloseSuccess,
        int Win32Error,
        double WrapperCallMs,
        int RetryCount,
        double RetryDelayMs)
    {
        internal string SamplingKey => $"{Environment}|{Launch}|{PayloadBytes}|{FileCount}|{Variant}|{Observation}";
    }

    private sealed record CellSummary(
        long PayloadBytes,
        int FileCount,
        string Variant,
        int SuccessfulCount,
        int AttemptCount,
        int FailedCount,
        double MedianMs,
        double LowerQuartileMs,
        double UpperQuartileMs,
        double CiLowerMs,
        double CiUpperMs,
        double MedianWriteToSyncGapMs);

    private sealed record GapSummary(
        long PayloadBytes,
        int FileCount,
        string Variant,
        int SampleCount,
        double MedianMs,
        double LowerQuartileMs,
        double UpperQuartileMs,
        double ReferenceMedianMs,
        double MedianDifferenceMs,
        double AllowedDifferenceMs,
        bool Stable,
        string Comparison);

    private sealed record PhaseMeasurement(
        long PayloadBytes,
        int FileCount,
        string Variant,
        int Launch,
        int Observation,
        double OpenMs,
        double FlushMs,
        double CloseMs,
        double TotalMs,
        double OpenPerFileMs,
        double FlushPerFileMs,
        double ClosePerFileMs,
        double RetryDelayMs);
}
