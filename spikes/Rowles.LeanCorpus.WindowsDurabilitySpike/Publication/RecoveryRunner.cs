using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rowles.DataForge.Workloads;
using Rowles.LeanCorpus.Document;
using Rowles.LeanCorpus.Index;
using Rowles.LeanCorpus.Index.Segment;
using Rowles.LeanCorpus.Search.Queries;
using Rowles.LeanCorpus.Search.Searcher;
using Rowles.LeanCorpus.Store;
using Rowles.LeanCorpus.WindowsDurabilitySpike.Analysis;
using Rowles.LeanCorpus.WindowsDurabilitySpike.Mechanisms;

namespace Rowles.LeanCorpus.WindowsDurabilitySpike.Publication;

internal static class RecoveryRunner
{
    private static readonly (string Name, DurabilitySpikeCheckpoint Checkpoint)[] CommonFailpoints =
    [
        ("after_segment_files_complete", DurabilitySpikeCheckpoint.AfterSegmentFilesComplete),
        ("after_data_files_persisted", DurabilitySpikeCheckpoint.AfterDataFilesPersisted),
        ("after_commit_marker_tmp_written", DurabilitySpikeCheckpoint.AfterCommitMarkerTempWritten),
        ("after_commit_marker_tmp_persisted", DurabilitySpikeCheckpoint.AfterCommitMarkerTempPersisted),
        ("before_publication_call", DurabilitySpikeCheckpoint.BeforePublicationCall),
        ("after_publication_call", DurabilitySpikeCheckpoint.AfterPublicationCall),
        ("after_directory_persist_attempt", DurabilitySpikeCheckpoint.AfterDirectoryPersistAttempt),
        ("after_commit_return", DurabilitySpikeCheckpoint.AfterCommitReturn)
    ];

    private static readonly (string Name, DurabilitySpikeCheckpoint Checkpoint)[] CompoundFailpoints =
    [
        ("after_compound_tmp_close_before_rename", DurabilitySpikeCheckpoint.AfterCompoundTempCloseBeforeRename),
        ("after_compound_rename", DurabilitySpikeCheckpoint.AfterCompoundRename),
        ("after_loose_members_deleted", DurabilitySpikeCheckpoint.AfterLooseMembersDeleted)
    ];

    private static readonly int[] FixedLookupOrdinals = [0, 1, 2, 7, 31, 127, 511, 1023, 2047, 4095, 8191, 9997, 9998, 9999];

    internal static int Run(SpikeArguments arguments)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Process recovery trials require Windows.");
        string dataRoot = Path.GetFullPath(arguments.Required("data-root"));
        string evidence = Path.GetFullPath(arguments.Required("evidence"));
        string datasetPath = Path.GetFullPath(arguments.Required("dataset"));
        ObservationNeutrality.Require(Path.GetFullPath(arguments.Required("neutrality-validation")));
        RecoveryDataset recoveryDataset = ReadRecoveryDataset(datasetPath);
        DatasetDescription identity = BuildRecoveryDatasetIdentity(recoveryDataset);
        EnsureEvidenceOutsideDataRoot(dataRoot, evidence);
        if (Directory.Exists(evidence))
            throw new IOException($"Recovery evidence directory already exists and will not be replaced: {evidence}");
        Directory.CreateDirectory(evidence);
        string orderPath = Path.Combine(evidence, "recovery-order.csv");
        List<RecoveryOrder> order = BuildProcessOrder();
        WriteOrder(orderPath, order);
        EnvironmentSnapshot.Write(evidence, "local_windows_vm", dataRoot, orderPath, 0);
        string controlRoot = Path.Combine(evidence, "reset-control", "process");
        string logRoot = Path.Combine(evidence, "logs");
        Directory.CreateDirectory(controlRoot);
        Directory.CreateDirectory(logRoot);

        File.WriteAllText(Path.Combine(evidence, "dataset-identity.json"),
            JsonSerializer.Serialize(identity, new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));

        var outcomes = new List<RecoveryOutcome>(order.Count);
        foreach (RecoveryOrder item in order)
        {
            string trialId = $"process-{item.Candidate}-{item.Representation}-{item.Failpoint}-trial-{item.Trial}";
            string indexPath = Path.Combine(dataRoot, "spike2-" + trialId);
            string controlPath = Path.Combine(controlRoot, trialId + ".json");
            string logPath = Path.Combine(logRoot, trialId + ".txt");
            RecoveryOutcome outcome = RunProcessChild(item, indexPath, controlPath, logPath,
                datasetPath, identity.FixedLookupIds);
            outcomes.Add(outcome);
            File.WriteAllText(Path.Combine(evidence, trialId + ".json"),
                JsonSerializer.Serialize(outcome, new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        }
        WriteRecoveryRows(Path.Combine(evidence, "recovery-trials.csv"), outcomes);
        int failed = outcomes.Count(static outcome => !outcome.ContractPassed);
        Console.WriteLine($"Process recovery: {outcomes.Count} trials, {failed} contract failures. Evidence: {evidence}");
        return failed == 0 ? 0 : 1;
    }

    internal static int FaultInjection(SpikeArguments arguments)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Fault-injection trials require Windows.");
        string dataRoot = Path.GetFullPath(arguments.Required("data-root"));
        string evidence = Path.GetFullPath(arguments.Required("evidence"));
        string datasetPath = Path.GetFullPath(arguments.Required("dataset"));
        ObservationNeutrality.Require(Path.GetFullPath(arguments.Required("neutrality-validation")));
        RecoveryDataset recoveryDataset = ReadRecoveryDataset(datasetPath);
        DatasetDescription identity = BuildRecoveryDatasetIdentity(recoveryDataset);
        EnsureEvidenceOutsideDataRoot(dataRoot, evidence);
        if (Directory.Exists(evidence))
            throw new IOException($"Fault-injection evidence directory already exists and will not be replaced: {evidence}");
        Directory.CreateDirectory(evidence);
        string controlRoot = Path.Combine(evidence, "reset-control", "fault-injection");
        string logRoot = Path.Combine(evidence, "logs");
        Directory.CreateDirectory(controlRoot);
        Directory.CreateDirectory(logRoot);
        var order = BuildFaultOrder();
        WriteOrder(Path.Combine(evidence, "fault-injection-order.csv"), order);
        File.WriteAllText(Path.Combine(evidence, "dataset-identity.json"),
            JsonSerializer.Serialize(identity, new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        EnvironmentSnapshot.Write(evidence, "local_windows_vm", dataRoot,
            Path.Combine(evidence, "fault-injection-order.csv"), 0);
        var rows = new List<FaultOutcome>(order.Count);
        foreach (FaultOrder item in order)
        {
            string trialId = $"fault-{item.Candidate}-{item.Representation}-{item.Operation}-{item.Edge}";
            string indexPath = Path.Combine(dataRoot, "spike2-" + trialId);
            string controlPath = Path.Combine(controlRoot, trialId + ".json");
            string logPath = Path.Combine(logRoot, trialId + ".txt");
            FaultOutcome outcome = RunFaultChild(item, indexPath, controlPath, logPath,
                datasetPath, identity.FixedLookupIds);
            rows.Add(outcome);
            File.WriteAllText(Path.Combine(evidence, trialId + ".json"),
                JsonSerializer.Serialize(outcome, new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        }
        WriteFaultRows(Path.Combine(evidence, "fault-injection.csv"), rows);
        int failed = rows.Count(static row => !row.ContractPassed);
        Console.WriteLine($"Fault injection: {rows.Count} trials, {failed} contract failures. Evidence: {evidence}");
        return failed == 0 ? 0 : 1;
    }

    internal static int Child(SpikeArguments arguments)
    {
        string mode = arguments.Required("mode");
        string candidate = arguments.Required("candidate");
        string representation = arguments.Required("representation");
        string indexPath = Path.GetFullPath(arguments.Required("trial-index"));
        string controlPath = Path.GetFullPath(arguments.Required("control"));
        string trialId = arguments.Required("trial-id");
        RecoveryDataset dataset = ReadRecoveryDataset(Path.GetFullPath(arguments.Required("dataset")));
        string? failpoint = arguments.Optional("failpoint", string.Empty);
        string faultOperation = arguments.Optional("fault-operation", string.Empty);
        string faultEdge = arguments.Optional("fault-edge", string.Empty);
        if (Directory.Exists(indexPath))
            throw new IOException($"Recovery trial index already exists: {indexPath}");

        Action<DurabilitySpikeCheckpoint, string?> checkpointAction = (checkpoint, path) =>
        {
            if (mode is not "process-crash" and not "hard-reset" ||
                !string.Equals(CheckpointName(checkpoint), failpoint, StringComparison.Ordinal))
                return;
            WriteControl(controlPath, new ControlRecord(trialId, candidate, representation,
                CheckpointName(checkpoint), mode, Environment.ProcessId, DateTimeOffset.UtcNow,
                checkpoint == DurabilitySpikeCheckpoint.AfterCommitReturn, null, null));
            if (mode == "process-crash")
                Environment.FailFast($"Spike 2 process failpoint {failpoint}");
            while (true)
                Thread.SpinWait(256);
        };
        Action<string, bool>? operationAction = mode == "fault"
            ? (operationId, before) =>
            {
                if (string.Equals(operationId, faultOperation, StringComparison.Ordinal) &&
                    string.Equals(faultEdge, before ? "before" : "after", StringComparison.Ordinal))
                    throw new InjectedDurabilityFailureException(operationId, faultEdge);
            }
            : null;
        var observer = new PublicationRunner.PublicationObserver(candidate, checkpointAction, operationAction);
        var directory = new MMapDirectory(indexPath);
        var writer = new Rowles.LeanCorpus.Index.Indexer.IndexWriter(directory,
            PublicationRunner.CreateConfig(representation == "compound"));
        using (DurabilitySpikeInstrumentation.Begin(observer))
        {
            PublicationRunner.AddBatch(writer, dataset.Records);
            if (mode == "fault")
            {
                try
                {
                    writer.Commit();
                }
                catch (InjectedDurabilityFailureException exception)
                {
                    WriteControl(controlPath, new ControlRecord(trialId, candidate, representation,
                        string.Empty, mode, Environment.ProcessId, DateTimeOffset.UtcNow,
                        false, exception.OperationId, exception.Edge));
                    return 42;
                }
                WriteControl(controlPath, new ControlRecord(trialId, candidate, representation,
                    string.Empty, mode, Environment.ProcessId, DateTimeOffset.UtcNow,
                    true, faultOperation, faultEdge));
                return 43;
            }

            writer.Commit();
            DurabilitySpikeInstrumentation.Checkpoint(DurabilitySpikeCheckpoint.AfterCommitReturn, indexPath);
        }
        return 0;
    }

    internal static int Inspect(SpikeArguments arguments)
    {
        string indexPath = Path.GetFullPath(arguments.Required("trial-index"));
        string output = Path.GetFullPath(arguments.Required("output"));
        bool afterCommitReturn = bool.Parse(arguments.Optional("after-commit-return", "false"));
        string controlPath = arguments.Optional("control", string.Empty);
        string trialId = arguments.Optional("trial-id", Path.GetFileName(indexPath));
        ControlRecord? control = File.Exists(controlPath)
            ? JsonSerializer.Deserialize<ControlRecord>(File.ReadAllText(controlPath))
            : null;
        string[] fixedLookupIds = BuildRecoveryDatasetIdentity(
            ReadRecoveryDataset(Path.GetFullPath(arguments.Required("dataset")))).FixedLookupIds;
        RecoveryInspection inspection = InspectTrial(indexPath, 10_000, afterCommitReturn, fixedLookupIds, control);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if (File.Exists(output))
            throw new IOException($"Recovery report already exists and will not be replaced: {output}");
        File.WriteAllText(output, JsonSerializer.Serialize(inspection, new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        Console.WriteLine(JsonSerializer.Serialize(new { trial_id = trialId, inspection.ContractPassed,
            inspection.SelectedGenerationClass, inspection.DocumentCount, inspection.DeepValidationPassed }));
        return inspection.ContractPassed ? 0 : 1;
    }

    internal static int ValidateDataset(SpikeArguments arguments)
    {
        RecoveryDataset dataset = ReadRecoveryDataset(Path.GetFullPath(arguments.Required("dataset")));
        DatasetDescription identity = BuildRecoveryDatasetIdentity(dataset);
        Console.WriteLine($"Recovery dataset contract passed: 100,000-record DataForge identity, measured ordinals 90,000..99,999, content SHA-256 {identity.Identity.ContentSha256}.");
        return 0;
    }

    internal static int ValidateCompoundCheckpointReachability(SpikeArguments arguments)
    {
        string dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(arguments.Required("data-root")));
        string output = Path.GetFullPath(arguments.Required("output"));
        RecoveryDataset dataset = ReadRecoveryDataset(Path.GetFullPath(arguments.Required("dataset")));
        EnsureEvidenceOutsideDataRoot(dataRoot, output);
        if (!Directory.Exists(dataRoot))
            throw new DirectoryNotFoundException($"Regression data root does not exist: {dataRoot}");

        const string candidate = "P0";
        const string representation = "compound";
        string indexPath = Path.Combine(dataRoot, "spike2-compound-checkpoint-regression");
        if (Directory.Exists(indexPath))
            throw new IOException($"Regression index already exists and will not be reused: {indexPath}");
        if (File.Exists(output))
            throw new IOException($"Regression evidence already exists and will not be replaced: {output}");
        if (dataset.Records.Length != 10_000)
            throw new InvalidDataException($"Expected exactly 10,000 recovery records, found {dataset.Records.Length}.");

        var expected = new[]
        {
            DurabilitySpikeCheckpoint.AfterCompoundTempCloseBeforeRename,
            DurabilitySpikeCheckpoint.AfterCompoundRename,
            DurabilitySpikeCheckpoint.AfterLooseMembersDeleted
        };
        var observer = new PublicationRunner.PublicationObserver(candidate);
        bool commitSucceeded = false;
        string? error = null;
        try
        {
            using var directory = new MMapDirectory(indexPath);
            using var writer = new Rowles.LeanCorpus.Index.Indexer.IndexWriter(directory,
                PublicationRunner.CreateConfig(compound: true));
            using (DurabilitySpikeInstrumentation.Begin(observer))
            {
                PublicationRunner.AddBatch(writer, dataset.Records);
                writer.Commit();
                commitSucceeded = true;
            }
        }
        catch (Exception exception)
        {
            error = $"{exception.GetType().Name}: {exception.Message}";
        }

        DurabilitySpikeCheckpoint[] reached = observer.Checkpoints;
        string[] expectedNames = expected.Select(CheckpointName).ToArray();
        string[] reachedNames = reached.Select(CheckpointName).ToArray();
        string[] missing = expected.Where(checkpoint => !reached.Contains(checkpoint))
            .Select(CheckpointName).ToArray();
        bool passed = commitSucceeded && missing.Length == 0 &&
                      expected.All(checkpoint => reached.Count(value => value == checkpoint) == 1);
        var report = new
        {
            schema_version = 1,
            validation = "compound_recovery_checkpoint_reachability",
            measured_source_sha = Environment.GetEnvironmentVariable("SPIKE_MEASURED_SOURCE_SHA") ??
                                  Environment.GetEnvironmentVariable("SPIKE_EXPERIMENT_SHA") ?? "unknown",
            candidate_id = candidate,
            representation,
            document_count = dataset.Records.Length,
            max_buffered_docs = 10_000,
            observer_active_for = new[] { "AddBatch", "Commit" },
            commit_succeeded = commitSucceeded,
            expected_failpoints = expectedNames,
            reached_failpoints = reachedNames,
            missing_failpoints = missing,
            passed,
            error
        };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false));
        Console.WriteLine(JsonSerializer.Serialize(new { passed, reached_failpoints = reachedNames, missing_failpoints = missing }));
        return passed ? 0 : 1;
    }

    private static RecoveryOutcome RunProcessChild(
        RecoveryOrder item,
        string indexPath,
        string controlPath,
        string logPath,
        string datasetPath,
        IReadOnlyList<string> fixedLookupIds)
    {
        string trialId = $"process-{item.Candidate}-{item.Representation}-{item.Failpoint}-trial-{item.Trial}";
        int exitCode = RunChildProcess([
            "crash-child", "--mode", "process-crash", "--candidate", item.Candidate,
            "--representation", item.Representation, "--failpoint", item.Failpoint,
            "--trial-index", indexPath, "--control", controlPath, "--trial-id", trialId,
            "--dataset", datasetPath
        ], logPath);
        ControlRecord? control = ReadControl(controlPath);
        bool failpointReached = control is not null && control.Failpoint == item.Failpoint &&
                                control.CommitReturned == (item.Failpoint == "after_commit_return");
        RecoveryInspection inspection = InspectTrial(indexPath, 10_000,
            afterCommitReturn: item.Failpoint == "after_commit_return", fixedLookupIds, control);
        return new RecoveryOutcome(trialId, "process_crash", item.Candidate, item.Representation,
            item.Failpoint, item.Trial, exitCode, failpointReached, inspection.SelectedGeneration,
            inspection.SelectedGenerationClass, inspection.DocumentCount, inspection.FixedLookupHits,
            inspection.FixedLookupIds,
            inspection.ReferencedFilesReadable, inspection.DeepValidationPassed, inspection.LeftoverTemporaryFiles,
            inspection.CommitMarkers, inspection.ContractPassed && failpointReached,
            control, inspection.Error, indexPath, logPath);
    }

    private static FaultOutcome RunFaultChild(
        FaultOrder item,
        string indexPath,
        string controlPath,
        string logPath,
        string datasetPath,
        IReadOnlyList<string> fixedLookupIds)
    {
        string trialId = $"fault-{item.Candidate}-{item.Representation}-{item.Operation}-{item.Edge}";
        string actualOperation = item.Operation == "directory_persist"
            ? item.Candidate == "P1" ? "directory_persist_prepublication" : "directory_persist_postpublication"
            : item.Operation;
        int exitCode = RunChildProcess([
            "crash-child", "--mode", "fault", "--candidate", item.Candidate,
            "--representation", item.Representation, "--fault-operation", actualOperation,
            "--fault-edge", item.Edge, "--trial-index", indexPath,
            "--control", controlPath, "--trial-id", trialId, "--dataset", datasetPath
        ], logPath);
        ControlRecord? control = ReadControl(controlPath);
        RecoveryInspection inspection = InspectTrial(indexPath, 10_000, afterCommitReturn: false, fixedLookupIds, control);
        bool injected = control is not null && !control.CommitReturned &&
                        control.OperationId == actualOperation && control.Edge == item.Edge;
        bool passed = injected && !control!.CommitReturned && inspection.ContractPassed;
        return new FaultOutcome(trialId, item.Candidate, item.Representation, item.Operation,
            item.Edge, actualOperation, injected, control?.CommitReturned ?? false,
            exitCode, inspection.SelectedGenerationClass, inspection.DocumentCount,
            inspection.FixedLookupHits, inspection.FixedLookupIds, inspection.DeepValidationPassed,
            inspection.ContractPassed, passed,
            control, inspection.Error, indexPath, logPath);
    }

    private static int RunChildProcess(IReadOnlyList<string> arguments, string logPath)
    {
        string processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Current process path is unavailable.");
        var start = new ProcessStartInfo { FileName = processPath, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using Process child = Process.Start(start) ?? throw new InvalidOperationException("Failed to start crash child.");
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        child.WaitForExit();
        string output = stdout.GetAwaiter().GetResult();
        string error = stderr.GetAwaiter().GetResult();
        File.WriteAllText(logPath,
            $"exit_code={child.ExitCode}\nstdout:\n{output}\nstderr:\n{error}", new UTF8Encoding(false));
        return child.ExitCode;
    }

    private static RecoveryInspection InspectTrial(
        string indexPath,
        int expectedDocuments,
        bool afterCommitReturn,
        IReadOnlyList<string> fixedLookupIds,
        ControlRecord? control)
    {
        int? generation = null;
        string generationClass = "none_or_old";
        int documentCount = 0;
        int fixedLookupHits = 0;
        bool referencedFilesReadable = true;
        bool deepValidationPassed = false;
        bool? selectedNew = false;
        string? error = null;
        IndexRecovery.RecoveryResult? recovery = null;
        try
        {
            recovery = IndexRecovery.RecoverLatestCommit(indexPath, cleanupOrphans: false);
            if (recovery is not null)
            {
                generation = recovery.Generation;
                generationClass = recovery.Generation == 1 ? "new_generation" : "unexpected_generation";
                selectedNew = recovery.Generation == 1;
                foreach (string segmentId in recovery.SegmentIds)
                {
                    foreach (string fileName in SegmentFileSet.Enumerate(indexPath, segmentId).FileNames)
                    {
                        string filePath = Path.Combine(indexPath, fileName);
                        try
                        {
                            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                FileShare.Read | FileShare.Write | FileShare.Delete);
                            if (stream.Length > 0)
                                _ = stream.ReadByte();
                        }
                        catch
                        {
                            referencedFilesReadable = false;
                        }
                    }
                }

                using var directory = new MMapDirectory(indexPath);
                IndexCheckResult validation = IndexValidator.Check(directory, new IndexCheckOptions { Deep = true });
                deepValidationPassed = validation.IsHealthy;
                if (!deepValidationPassed)
                    error = string.Join("; ", validation.Issues);
                using var searcher = new IndexSearcher(directory);
                documentCount = searcher.Search(new MatchAllDocsQuery(), 1).TotalHits;
                foreach (string id in fixedLookupIds)
                {
                    if (searcher.Search(new TermQuery("id", id), 1).TotalHits == 1)
                        fixedLookupHits++;
                }
            }
        }
        catch (Exception exception)
        {
            error = $"{exception.GetType().Name}: {exception.Message}";
            referencedFilesReadable = false;
            deepValidationPassed = false;
            selectedNew = null;
        }

        string[] allFiles = Directory.Exists(indexPath)
            ? Directory.EnumerateFiles(indexPath).Select(Path.GetFileName).Where(static name => name is not null)
                .Select(static name => name!).OrderBy(static name => name, StringComparer.Ordinal).ToArray()
            : [];
        string[] temporaries = allFiles.Where(name => name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                                                       name.EndsWith(".pending", StringComparison.OrdinalIgnoreCase)).ToArray();
        var markers = new List<MarkerInventory>();
        foreach (string fileName in allFiles.Where(static name => name.StartsWith("segments_", StringComparison.Ordinal)))
        {
            string filePath = Path.Combine(indexPath, fileName);
            try
            {
                var info = new FileInfo(filePath);
                markers.Add(new MarkerInventory(fileName, info.Length, HashFile(filePath),
                    fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase),
                    fileName.EndsWith(".pending", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception exception)
            {
                markers.Add(new MarkerInventory(fileName, -1, "unreadable", false, false));
                error ??= $"{exception.GetType().Name}: {exception.Message}";
            }
        }

        bool noGeneration = recovery is null;
        bool completeNew = recovery is not null && generation == 1 && documentCount == expectedDocuments &&
                           fixedLookupHits == FixedLookupOrdinals.Length && referencedFilesReadable && deepValidationPassed;
        bool contractPassed = afterCommitReturn
            ? completeNew
            : noGeneration || completeNew;
        return new RecoveryInspection(generation, generationClass, selectedNew, documentCount,
            fixedLookupHits, FixedLookupOrdinals, fixedLookupIds.ToArray(), referencedFilesReadable, deepValidationPassed,
            temporaries, markers, allFiles, contractPassed, control, error);
    }

    private static List<RecoveryOrder> BuildProcessOrder()
    {
        var rows = new List<RecoveryOrder>();
        string[] candidates = ["P0", "P1", "P2"];
        foreach (string candidate in candidates)
        foreach (string representation in new[] { "loose", "compound" })
        {
            IEnumerable<(string Name, DurabilitySpikeCheckpoint Checkpoint)> failpoints =
                candidate == "P1" ? CommonFailpoints.Where(static item => item.Name != "after_directory_persist_attempt") : CommonFailpoints;
            if (representation == "compound")
                failpoints = failpoints.Concat(CompoundFailpoints);
            foreach (var failpoint in failpoints)
            for (int trial = 1; trial <= 3; trial++)
                rows.Add(new RecoveryOrder(candidate, representation, failpoint.Name, trial));
        }
        return rows;
    }

    private static List<FaultOrder> BuildFaultOrder()
    {
        var rows = new List<FaultOrder>();
        foreach (string candidate in new[] { "P0", "P1", "P2" })
        foreach (string representation in new[] { "loose", "compound" })
        foreach (string operation in new[] { "temp_marker_file_persist", "publication_call", "directory_persist" })
        foreach (string edge in new[] { "before", "after" })
            rows.Add(new FaultOrder(candidate, representation, operation, edge));
        return rows;
    }

    private static void WriteOrder(string path, IReadOnlyList<RecoveryOrder> rows)
    {
        using var csv = new CsvFile(path, "candidate_id", "representation", "failpoint", "trial");
        foreach (RecoveryOrder row in rows)
            csv.WriteRow(row.Candidate, row.Representation, row.Failpoint, row.Trial);
    }

    private static void WriteOrder(string path, IReadOnlyList<FaultOrder> rows)
    {
        using var csv = new CsvFile(path, "candidate_id", "representation", "operation", "edge");
        foreach (FaultOrder row in rows)
            csv.WriteRow(row.Candidate, row.Representation, row.Operation, row.Edge);
    }

    private static void WriteRecoveryRows(string path, IReadOnlyList<RecoveryOutcome> rows)
    {
        using var csv = new CsvFile(path,
            "trial_id", "termination_class", "candidate_id", "representation", "failpoint", "trial",
            "child_exit_code", "failpoint_reached", "selected_generation", "selected_generation_class",
            "document_count", "fixed_lookup_hits", "fixed_lookup_ids", "referenced_files_readable", "deep_validation_passed",
            "leftover_temporary_files", "commit_marker_inventory_json", "contract_passed", "error", "index_path", "log_path");
        foreach (RecoveryOutcome row in rows)
            csv.WriteRow(row.TrialId, row.TerminationClass, row.Candidate, row.Representation,
                row.Failpoint, row.Trial, row.ChildExitCode, row.FailpointReached, row.SelectedGeneration,
                row.SelectedGenerationClass, row.DocumentCount, row.FixedLookupHits, string.Join(';', row.FixedLookupIds), row.ReferencedFilesReadable,
                row.DeepValidationPassed, string.Join(';', row.LeftoverTemporaryFiles),
                JsonSerializer.Serialize(row.CommitMarkers), row.ContractPassed, row.Error, row.IndexPath, row.LogPath);
    }

    private static void WriteFaultRows(string path, IReadOnlyList<FaultOutcome> rows)
    {
        using var csv = new CsvFile(path,
            "trial_id", "candidate_id", "representation", "operation", "edge", "actual_operation_id",
            "injected_exception_observed", "commit_reported_success", "child_exit_code",
            "selected_generation_class", "document_count", "fixed_lookup_hits", "fixed_lookup_ids", "deep_validation_passed",
            "recovery_contract_passed", "contract_passed", "error", "index_path", "log_path");
        foreach (FaultOutcome row in rows)
            csv.WriteRow(row.TrialId, row.Candidate, row.Representation, row.Operation, row.Edge,
                row.ActualOperationId, row.InjectedExceptionObserved, row.CommitReportedSuccess,
                row.ChildExitCode, row.SelectedGenerationClass, row.DocumentCount,
                row.FixedLookupHits, string.Join(';', row.FixedLookupIds),
                row.DeepValidationPassed, row.RecoveryContractPassed, row.ContractPassed,
                row.Error ?? string.Empty, row.IndexPath, row.LogPath);
    }

    private static string CheckpointName(DurabilitySpikeCheckpoint checkpoint) => checkpoint switch
    {
        DurabilitySpikeCheckpoint.AfterSegmentFilesComplete => "after_segment_files_complete",
        DurabilitySpikeCheckpoint.AfterDataFilesPersisted => "after_data_files_persisted",
        DurabilitySpikeCheckpoint.AfterCommitMarkerTempWritten => "after_commit_marker_tmp_written",
        DurabilitySpikeCheckpoint.AfterCommitMarkerTempPersisted => "after_commit_marker_tmp_persisted",
        DurabilitySpikeCheckpoint.BeforePublicationCall => "before_publication_call",
        DurabilitySpikeCheckpoint.AfterPublicationCall => "after_publication_call",
        DurabilitySpikeCheckpoint.AfterDirectoryPersistAttempt => "after_directory_persist_attempt",
        DurabilitySpikeCheckpoint.AfterCommitReturn => "after_commit_return",
        DurabilitySpikeCheckpoint.AfterCompoundTempCloseBeforeRename => "after_compound_tmp_close_before_rename",
        DurabilitySpikeCheckpoint.AfterCompoundRename => "after_compound_rename",
        DurabilitySpikeCheckpoint.AfterLooseMembersDeleted => "after_loose_members_deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(checkpoint), checkpoint, null)
    };

    private static DatasetDescription BuildRecoveryDatasetIdentity(RecoveryDataset dataset)
        => new(dataset.Identity, dataset.Records.Length, 42,
            FixedLookupOrdinals.Select(ordinal => dataset.Records[ordinal].Id).ToArray());

    private static RecoveryDataset ReadRecoveryDataset(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The pre-generated measured-range recovery dataset is missing.", path);
        RecoveryDatasetFile file = JsonSerializer.Deserialize<RecoveryDatasetFile>(File.ReadAllText(path))
            ?? throw new InvalidDataException("The recovery dataset file is empty or malformed.");
        if (file.DatasetIdentity is null || file.Records is null ||
            file.Seed != 42 || file.FirstOrdinal != 90_000 || file.LastOrdinal != 99_999 ||
            file.Records.Length != 10_000 || file.DatasetIdentity.RecordCount != 100_000 ||
            file.DatasetIdentity.Seed != 42 || file.DatasetIdentity.ProfileId != "leancorpus-search")
            throw new InvalidDataException("The recovery dataset does not match the frozen DataForge measured range contract.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < file.Records.Length; index++)
        {
            SearchRecord? record = file.Records[index];
            if (record is null || record.Ordinal != 90_000 + index ||
                string.IsNullOrWhiteSpace(record.Id) || !ids.Add(record.Id))
                throw new InvalidDataException("The recovery dataset records are not the unique, ordered 90,000..99,999 DataForge slice.");
        }
        return new RecoveryDataset(file.DatasetIdentity, file.Records);
    }

    private static void WriteControl(string path, ControlRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record) + "\n");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    private static ControlRecord? ReadControl(string path)
        => File.Exists(path) ? JsonSerializer.Deserialize<ControlRecord>(File.ReadAllText(path)) : null;

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void EnsureEvidenceOutsideDataRoot(string dataRoot, string evidence)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)) + Path.DirectorySeparatorChar;
        string result = Path.GetFullPath(evidence) + Path.DirectorySeparatorChar;
        if (result.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Recovery evidence and control records must be outside the index volume directory.");
    }

    private sealed record RecoveryOrder(string Candidate, string Representation, string Failpoint, int Trial);
    private sealed record FaultOrder(string Candidate, string Representation, string Operation, string Edge);
    private sealed record DatasetDescription(Rowles.DataForge.DataForgeDatasetIdentity Identity, int RecordCount, ulong Seed, string[] FixedLookupIds);
    private sealed record RecoveryDatasetFile(Rowles.DataForge.DataForgeDatasetIdentity DatasetIdentity,
        ulong Seed, int FirstOrdinal, int LastOrdinal, SearchRecord[] Records);
    private sealed record RecoveryDataset(Rowles.DataForge.DataForgeDatasetIdentity Identity, SearchRecord[] Records);
    private sealed record ControlRecord(string TrialId, string Candidate, string Representation, string Failpoint,
        string Mode, int ProcessId, DateTimeOffset Utc, bool CommitReturned, string? OperationId, string? Edge);
    private sealed record MarkerInventory(string FileName, long SizeBytes, string Sha256, bool Temporary, bool Pending);
    private sealed record RecoveryInspection(int? SelectedGeneration, string SelectedGenerationClass,
        bool? SelectedNewGeneration, int DocumentCount, int FixedLookupHits, int[] FixedLookupOrdinals,
        string[] FixedLookupIds,
        bool ReferencedFilesReadable, bool DeepValidationPassed, string[] LeftoverTemporaryFiles,
        IReadOnlyList<MarkerInventory> CommitMarkers, string[] AllFiles, bool ContractPassed,
        ControlRecord? ControlRecord, string? Error);
    private sealed record RecoveryOutcome(string TrialId, string TerminationClass, string Candidate,
        string Representation, string Failpoint, int Trial, int ChildExitCode, bool FailpointReached,
        int? SelectedGeneration, string SelectedGenerationClass, int DocumentCount, int FixedLookupHits,
        string[] FixedLookupIds,
        bool ReferencedFilesReadable, bool DeepValidationPassed, string[] LeftoverTemporaryFiles,
        IReadOnlyList<MarkerInventory> CommitMarkers, bool ContractPassed, ControlRecord? ControlRecord,
        string? Error, string IndexPath, string LogPath);
    private sealed record FaultOutcome(string TrialId, string Candidate, string Representation,
        string Operation, string Edge, string ActualOperationId, bool InjectedExceptionObserved,
        bool CommitReportedSuccess, int ChildExitCode, string SelectedGenerationClass, int DocumentCount,
        int FixedLookupHits, string[] FixedLookupIds, bool DeepValidationPassed,
        bool RecoveryContractPassed, bool ContractPassed,
        ControlRecord? ControlRecord, string? Error, string IndexPath, string LogPath);

    private sealed class InjectedDurabilityFailureException(string operationId, string edge)
        : IOException($"Injected durability failure {edge} {operationId}.")
    {
        internal string OperationId { get; } = operationId;
        internal string Edge { get; } = edge;
    }
}

internal static class EvidenceVerifier
{
    private static readonly string[] RequiredRootFiles =
    [
        "environment.json", "source-map.md", "dataset-identity.json", "recovery-dataset.json", "source-and-assembly-hashes.json",
        "analysis-provenance.json", "instrumentation-audit.md", "publication-semantics.md",
        "mechanism-order.csv", "publication-order.csv", "mechanism-summary.md", "mechanism-summary.json",
        "hosted-replication-summary.md", "hosted-replication-summary.json", "publication-summary.md",
        "publication-summary.json", "trace-index.csv"
    ];

    internal static int Run(SpikeArguments arguments)
    {
        string root = Path.GetFullPath(arguments.Required("root"));
        foreach (string file in RequiredRootFiles)
        {
            string path = Path.Combine(root, file);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Required final evidence artefact is missing: {file}", path);
        }
        foreach (string file in new[]
                 {
                     "windows-file-mechanism.csv", "windows-file-mechanism-per-file.csv",
                     "windows-clean-reflush.csv", "publication-trials.csv", "recovery-trials.csv",
                     "fault-injection.csv"
                 })
        {
            if (!Directory.EnumerateFiles(root, file, SearchOption.AllDirectories).Any())
                throw new FileNotFoundException($"Required raw evidence artefact is missing: {file}");
        }
        foreach (string directory in new[] { "logs", "traces", "reset-control", "hosted-replication" })
        {
            string path = Path.Combine(root, directory);
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException($"Required evidence directory is missing: {directory}");
        }
        string traceIndex = Path.Combine(root, "trace-index.csv");
        string[][] traces = CsvReader.Read(traceIndex).ToArray();
        if (traces.Length != 10 || traces.Any(static row => row.Length != 11 ||
                !bool.TryParse(row[9], out bool success) || !success || row[8] != "passed" || row[2].Length != 64))
            throw new InvalidDataException($"The trace index requires ten successful mechanism/publication traces; found {traces.Length} rows.");
        string[] hostedEnvironments = Directory.EnumerateFiles(Path.Combine(root, "hosted-replication"),
            "environment.json", SearchOption.AllDirectories).ToArray();
        if (hostedEnvironments.Length != 6)
            throw new InvalidDataException($"Hosted replication requires six independently identified launches; found {hostedEnvironments.Length}.");

        string output = Path.Combine(root, "sha256sums.txt");
        if (File.Exists(output))
            throw new IOException($"Checksum file already exists and will not be replaced: {output}");
        var rows = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, output, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .Select(path => $"{RecoveryRunnerHash(path)}  {Path.GetRelativePath(root, path).Replace('\\', '/')}");
        File.WriteAllLines(output, rows, new UTF8Encoding(false));
        Console.WriteLine("Checksums written to " + output);
        return 0;
    }

    private static string RecoveryRunnerHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
