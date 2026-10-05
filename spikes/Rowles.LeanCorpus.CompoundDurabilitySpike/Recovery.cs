using System.Globalization;
using System.Text;
using System.Text.Json;
using Rowles.DataForge.Workloads;
using Rowles.LeanCorpus.Diagnostics;
using Rowles.LeanCorpus.Document;
using Rowles.LeanCorpus.Index;
using Rowles.LeanCorpus.Index.Indexer;
using Rowles.LeanCorpus.Search.Queries;
using Rowles.LeanCorpus.Search.Searcher;
using Rowles.LeanCorpus.Store;

namespace Rowles.LeanCorpus.CompoundDurabilitySpike;

internal static partial class SpikeRunner
{
    private static readonly string[] RecoveryCheckpoints =
    [
        "after_loose_segment_complete",
        "after_compound_tmp_close_before_rename",
        "after_compound_rename",
        "after_loose_members_deleted",
        "after_changed_files_persisted",
        "after_commit_marker_tmp_persisted",
        "after_commit_marker_renamed",
        "after_commit_marker_final_persisted",
        "after_directory_persist_attempt",
        "after_commit_return"
    ];

    public static async Task<int> RunRecoveryAsync(string root, Arguments arguments)
    {
        var paths = new SpikePaths(root);
        string platform = arguments.Required("platform");
        string runId = File.ReadAllText(Path.Combine(root, "run-id.txt")).Trim();
        string baseSha = File.ReadAllText(Path.Combine(root, "base-sha.txt")).Trim();
        string spikeSha = File.ReadAllText(Path.Combine(root, "spike-sha.txt")).Trim();
        int completedTrials = 0;

        foreach (string representation in new[] { "loose", "compound" })
        {
            foreach (string checkpoint in RecoveryCheckpoints)
            {
                bool notApplicable =
                    (representation == "loose" && checkpoint.StartsWith("after_compound_", StringComparison.Ordinal))
                    || checkpoint == "after_loose_members_deleted" && representation == "loose"
                    || checkpoint == "after_commit_marker_final_persisted";
                if (notApplicable)
                {
                    var row = new CsvRowBuilder(CsvSchemas.Recovery);
                    row.Set("run_id", runId);
                    row.Set("platform_id", platform);
                    row.Set("representation", representation);
                    row.Set("checkpoint", checkpoint);
                    row.Set("trial", 0);
                    row.Set("base_sha", baseSha);
                    row.Set("spike_sha", spikeSha);
                    row.Set("termination_kind", "not_applicable");
                    row.Set("recovered_generation_class", string.Empty);
                    row.Set("error", checkpoint == "after_commit_marker_final_persisted"
                        ? "N/A: production has no explicit final-path commit-marker persistence request."
                        : "N/A: this representation has no compound packing boundary.");
                    row.Append(paths.RecoveryTrialsPath);
                    continue;
                }

                for (int trial = 1; trial <= 3; trial++)
                {
                    string trialId = $"recovery-{representation}-{checkpoint}-t{trial}";
                    string trialPath = paths.TrialPath(trialId);
                    if (Directory.Exists(trialPath))
                        throw new IOException($"Recovery trial path already exists: {trialId}");
                    SpikeInfrastructure.CopyDirectory(paths.BaselinePath(representation), trialPath);

                    string controlLog = Path.Combine(paths.LogsDirectory, trialId + ".control.log");
                    string crashLog = Path.Combine(paths.LogsDirectory, trialId + ".crash.log");
                    var crashArguments = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["mode"] = "recovery-worker",
                        ["root"] = root,
                        ["phase"] = "crash",
                        ["index-path"] = trialPath,
                        ["representation"] = representation,
                        ["checkpoint"] = checkpoint
                    };
                    ProcessResult crash = await SpikeInfrastructure.RunWorkerAsync(
                        root,
                        crashLog,
                        crashArguments,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["SPIKE_FAILPOINT"] = checkpoint,
                            ["SPIKE_CONTROL_LOG"] = controlLog
                        });

                    string checkResultPath = Path.Combine(paths.LogsDirectory, trialId + ".recovery.json");
                    string checkLog = Path.Combine(paths.LogsDirectory, trialId + ".check.log");
                    var checkArguments = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["mode"] = "recovery-worker",
                        ["root"] = root,
                        ["phase"] = "check",
                        ["index-path"] = trialPath,
                        ["result-path"] = checkResultPath
                    };
                    ProcessResult check = await SpikeInfrastructure.RunWorkerAsync(root, checkLog, checkArguments);
                    RecoveryCheckResult? recovered = File.Exists(checkResultPath)
                        ? JsonSerializer.Deserialize<RecoveryCheckResult>(File.ReadAllText(checkResultPath))
                        : null;
                    string directoryOutcome = ReadControlValue(controlLog, "directory_persist_outcome");
                    string terminationKind = File.Exists(controlLog) ? "fail_fast" : "checkpoint_not_reached";
                    bool commitReturned = checkpoint == "after_commit_return" && File.Exists(controlLog);
                    string? error = null;
                    if (!File.Exists(controlLog))
                        error = $"Expected failpoint '{checkpoint}' was not reached (child exit {crash.ExitCode}).";
                    if (check.ExitCode != 0 || recovered is null)
                        error = AppendError(error, $"Recovery checker failed (exit {check.ExitCode}).");
                    if (recovered?.RecoveredGenerationClass == "invalid_partial")
                        error = AppendError(error, "Recovery exposed an invalid partial generation.");
                    if (commitReturned && recovered?.RecoveredGenerationClass != "new_complete")
                        error = AppendError(error, "Recovery after successful Commit return did not expose the new complete generation.");
                    if (commitReturned && recovered?.TemporaryFileCount != 0)
                        error = AppendError(error, $"Recovery after successful Commit return left {recovered?.TemporaryFileCount} temporary files.");
                    if (recovered is not null && recovered.MissingFileCount != 0)
                        error = AppendError(error, $"Recovery has {recovered.MissingFileCount} missing referenced files.");
                    if (recovered is not null && recovered.TruncatedFileCount != 0)
                        error = AppendError(error, $"Recovery has {recovered.TruncatedFileCount} unreadable or truncated referenced files.");
                    bool contractViolation = recovered is not null
                        && (recovered.RecoveredGenerationClass is "invalid_partial" or "unopenable"
                            || recovered.MissingFileCount != 0
                            || recovered.TruncatedFileCount != 0
                            || commitReturned && (recovered.RecoveredGenerationClass != "new_complete" || recovered.TemporaryFileCount != 0));

                    var row = new CsvRowBuilder(CsvSchemas.Recovery);
                    row.Set("run_id", runId);
                    row.Set("platform_id", platform);
                    row.Set("representation", representation);
                    row.Set("checkpoint", checkpoint);
                    row.Set("trial", trial);
                    row.Set("base_sha", baseSha);
                    row.Set("spike_sha", spikeSha);
                    row.Set("termination_kind", terminationKind);
                    row.Set("commit_returned", commitReturned);
                    row.Set("reopen_ok", recovered?.ReopenOk ?? false);
                    row.Set("recovered_doc_count", recovered?.RecoveredDocumentCount);
                    row.Set("recovered_generation_class", recovered?.RecoveredGenerationClass ?? "unopenable");
                    row.Set("id_lookup_pass", recovered?.IdLookupPass ?? false);
                    row.Set("index_integrity_pass", recovered?.IndexIntegrityPass ?? false);
                    row.Set("missing_file_count", recovered?.MissingFileCount ?? 0);
                    row.Set("truncated_file_count", recovered?.TruncatedFileCount ?? 0);
                    row.Set("tmp_file_count", recovered?.TemporaryFileCount ?? 0);
                    row.Set("directory_persist_outcome", directoryOutcome);
                    row.Set("error", error ?? recovered?.Error ?? string.Empty);
                    row.Append(paths.RecoveryTrialsPath);
                    completedTrials++;

                    if (recovered is null || !string.IsNullOrEmpty(error))
                        PreserveFailedTrial(paths, trialPath, trialId);
                    else
                        Directory.Delete(trialPath, recursive: true);
                    if (contractViolation)
                        throw new InvalidDataException($"Recovery contract violation at {representation}/{checkpoint}/trial {trial}; platform execution stopped after preserving the trial.");
                }
            }
        }

        Console.WriteLine($"Recovery crash trials completed={completedTrials}");
        return 0;
    }

    public static Task<int> RunRecoveryWorkerAsync(string root, Arguments arguments)
    {
        string phase = arguments.Required("phase");
        string path = arguments.Required("index-path");
        if (phase == "crash")
        {
            var paths = new SpikePaths(root);
            string representation = arguments.Required("representation");
            var observer = new SpikeObserver(
                Environment.GetEnvironmentVariable("SPIKE_FAILPOINT"),
                Environment.GetEnvironmentVariable("SPIKE_CONTROL_LOG"));
            observer.Attach();
            using var directory = new MMapDirectory(path);
            using var writer = new IndexWriter(directory, CreateConfig(representation, durable: true));
            foreach (LeanDocument document in ReadDocuments(paths, Dataset.BatchStart, Dataset.BatchCount))
                writer.AddDocument(document);
            observer.BeginCommit();
            writer.Commit();
            observer.Checkpoint("after_commit_return");
            return Task.FromResult(0);
        }

        if (phase != "check")
            throw new ArgumentException($"Unknown recovery phase '{phase}'.");
        RecoveryCheckResult result = CheckRecoveredIndex(path, Dataset.BatchCount, new SpikePaths(root));
        string resultPath = arguments.Required("result-path");
        File.WriteAllText(resultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false));
        Console.WriteLine($"recovered_class={result.RecoveredGenerationClass} docs={result.RecoveredDocumentCount} healthy={result.IndexIntegrityPass}");
        return Task.FromResult(0);
    }

    private static RecoveryCheckResult CheckRecoveredIndex(string path, int batchCount, SpikePaths paths)
    {
        bool reopenOk = false;
        int documentCount = 0;
        bool lookups = false;
        bool integrity = false;
        int missingFiles = 0;
        int truncatedFiles = 0;
        string? error = null;
        try
        {
            using (var searcher = new IndexSearcher(new MMapDirectory(path)))
            {
                reopenOk = true;
                documentCount = searcher.Stats.TotalDocCount;
                SearchRecord[] lookupRecords = LookupOffsets
                    .Select(offset => Dataset.ReadRecord(paths, Dataset.BatchStart + offset))
                    .ToArray();
                bool newGeneration = documentCount == Dataset.BaselineCount + batchCount;
                bool previousGeneration = documentCount == Dataset.BaselineCount;
                lookups = lookupRecords.All(record => searcher.Search(new TermQuery("id", record.Id), 1).TotalHits == (newGeneration ? 1 : 0));
                if (!newGeneration && !previousGeneration)
                    lookups = false;
            }

            using var directory = new MMapDirectory(path);
            IndexCheckResult result = IndexValidator.Check(directory, new IndexCheckOptions { Deep = true });
            integrity = result.IsHealthy && result.FilesChecked > 0;
            missingFiles = result.DetailedIssues
                .Where(static issue => issue.Severity == IndexCheckSeverity.Error && issue.Code == IndexCheckIssueCodes.RequiredFileMissing)
                .Select(static issue => issue.FileName ?? issue.Code)
                .Distinct(StringComparer.Ordinal)
                .Count();
            truncatedFiles = result.DetailedIssues
                .Where(static issue => issue.Severity == IndexCheckSeverity.Error && issue.Code != IndexCheckIssueCodes.RequiredFileMissing)
                .Select(static issue => issue.FileName ?? issue.Code)
                .Distinct(StringComparer.Ordinal)
                .Count();
            if (!integrity)
                error = string.Join(" | ", result.Issues);
        }
        catch (Exception exception)
        {
            error = exception.ToString();
        }

        int temporaryCount = Directory.Exists(path)
            ? Directory.EnumerateFiles(path).Count(static file => Path.GetFileName(file).EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            : 0;
        string generationClass = !reopenOk
            ? "unopenable"
            : documentCount == Dataset.BaselineCount && lookups && integrity && missingFiles == 0 && truncatedFiles == 0
                ? "previous_complete"
                : documentCount == Dataset.BaselineCount + batchCount && lookups && integrity && missingFiles == 0 && truncatedFiles == 0
                    ? "new_complete"
                    : "invalid_partial";
        return new RecoveryCheckResult(
            reopenOk,
            documentCount,
            generationClass,
            lookups,
            integrity,
            missingFiles,
            truncatedFiles,
            temporaryCount,
            error ?? string.Empty);
    }

    private static string ReadControlValue(string path, string key)
    {
        if (!File.Exists(path))
            return string.Empty;
        string prefix = key + "=";
        return File.ReadLines(path).FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..] ?? string.Empty;
    }

    private sealed record RecoveryCheckResult(
        bool ReopenOk,
        int RecoveredDocumentCount,
        string RecoveredGenerationClass,
        bool IdLookupPass,
        bool IndexIntegrityPass,
        int MissingFileCount,
        int TruncatedFileCount,
        int TemporaryFileCount,
        string Error);
}
