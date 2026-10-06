using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Rowles.DataForge;
using Rowles.DataForge.Workloads;
using Rowles.LeanCorpus.Diagnostics;
using Rowles.LeanCorpus.Document;
using Rowles.LeanCorpus.Document.Fields;
using Rowles.LeanCorpus.Index;
using Rowles.LeanCorpus.Index.Format;
using Rowles.LeanCorpus.Index.Indexer;
using Rowles.LeanCorpus.Search.Queries;
using Rowles.LeanCorpus.Search.Searcher;
using Rowles.LeanCorpus.Store;

namespace Rowles.LeanCorpus.CompoundDurabilitySpike;

internal static partial class SpikeRunner
{
    private static readonly int[] LookupOffsets = [0, 1, 2, 7, 31, 127, 511, 1023, 2047, 4095, 8191, 9997, 9998, 9999];

    public static Task<int> BuildBaselineAsync(string root, Arguments arguments)
    {
        var paths = new SpikePaths(root);
        string representation = arguments.Required("representation");
        string path = arguments.Required("index-path");
        EnsureDataset(paths);
        Directory.CreateDirectory(path);

        using (var directory = new MMapDirectory(path))
        using (var writer = new IndexWriter(directory, CreateConfig(representation, durable: true)))
        {
            for (int segment = 0; segment < 9; segment++)
            {
                int start = segment * 10_000;
                foreach (LeanDocument document in ReadDocuments(paths, start, 10_000))
                    writer.AddDocument(document);
                writer.Commit();
            }
        }

        IndexTopology topology = Topology.Read(path, representation, inspectEveryDocument: true);
        if (!Topology.HasExpectedBaseline(topology))
            throw new InvalidDataException($"Baseline '{representation}' did not produce exactly nine logical 10,000-document segments.");
        ValidationResult validation = ValidateIndex(path, Dataset.BaselineCount, representation, []);
        if (!validation.Pass)
            throw new InvalidDataException($"Baseline '{representation}' failed validation: {validation.Error}");

        Console.WriteLine($"baseline={representation} documents={Dataset.BaselineCount} segments={topology.SegmentCount} path={path}");
        return Task.FromResult(0);
    }

    public static async Task<int> RunProductionLaunchAsync(string root, Arguments arguments)
    {
        var paths = new SpikePaths(root);
        string platform = arguments.Required("platform");
        int launch = arguments.Int32("launch");
        if (launch is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(arguments), "Spike 1B production launches are 1 through 5.");
        string runId = File.ReadAllText(Path.Combine(root, "run-id.txt")).Trim();
        string experimentSha = File.ReadAllText(Path.Combine(root, "experiment-sha.txt")).Trim();
        string baseSha = File.ReadAllText(Path.Combine(root, "base-sha.txt")).Trim();
        List<(int CellOrder, string CellId)> cells = ReadExecutionOrder(root, launch);
        if (cells.Count != 12)
            throw new InvalidDataException($"Launch {launch} has {cells.Count} resolved production cells; expected 12.");

        foreach (var (cellOrder, cellId) in cells)
        {
            Cell cell = Cell.Parse(cellId);
            for (int observation = 0; observation <= 5; observation++)
            {
                bool warmup = observation == 0;
                string trialId = $"prod-l{launch}-c{cellOrder}-o{observation}-{cellId}";
                if (Csv.Read(paths.ProductionTrialsPath).Any(row =>
                    row["launch"] == launch.ToString(CultureInfo.InvariantCulture)
                    && row["cell_order"] == cellOrder.ToString(CultureInfo.InvariantCulture)
                    && row["observation"] == observation.ToString(CultureInfo.InvariantCulture)))
                    throw new IOException($"Observation already exists for launch {launch}, cell {cellId}, observation {observation}; failed observations are never replaced.");

                string trialPath = paths.TrialPath(trialId);
                if (Directory.Exists(trialPath))
                    throw new IOException($"Trial path already exists: {trialId}");
                if (cell.Lifecycle != "fresh")
                    SpikeInfrastructure.CopyDirectory(paths.BaselinePath(cell.Representation), trialPath);
                else
                    Directory.CreateDirectory(trialPath);

                string logPath = Path.Combine(paths.LogsDirectory, trialId + ".log");
                var workerArguments = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["mode"] = "production-worker",
                    ["root"] = root,
                    ["platform"] = platform,
                    ["launch"] = launch.ToString(CultureInfo.InvariantCulture),
                    ["cell-order"] = cellOrder.ToString(CultureInfo.InvariantCulture),
                    ["observation"] = observation.ToString(CultureInfo.InvariantCulture),
                    ["warmup"] = warmup.ToString().ToLowerInvariant(),
                    ["trial-id"] = trialId,
                    ["representation"] = cell.Representation,
                    ["durable"] = cell.Durable.ToString().ToLowerInvariant(),
                    ["lifecycle"] = cell.Lifecycle,
                    ["run-id"] = runId,
                    ["experiment-sha"] = experimentSha,
                    ["base-sha"] = baseSha
                };
                ProcessResult result = await SpikeInfrastructure.RunWorkerAsync(root, logPath, workerArguments);
                EnsureProductionFailureRecorded(paths, workerArguments, result, logPath);
                if (result.ExitCode != 0)
                    Console.Error.WriteLine($"trial {trialId} exited {result.ExitCode}: {result.StandardError.Trim()}");
            }
        }

        WriteProductionPairsForLaunch(paths, launch);
        foreach (var group in Csv.Read(paths.ProductionPairsPath)
                     .Where(row => row["launch"] == launch.ToString(CultureInfo.InvariantCulture)
                         && row["observation"] != "0" && row["pair_status"] != "valid")
                     .GroupBy(row => (Lifecycle: row["lifecycle"], Durable: row["durable"])))
        {
            int invalidPairs = group.Count();
            if (invalidPairs > 1)
                throw new InvalidDataException($"Launch {launch} comparison cell lifecycle={group.Key.Lifecycle}, durable={group.Key.Durable} has {invalidPairs} failed or topology-mismatched pairs; stop this platform for review.");
        }

        return 0;
    }

    public static Task<int> RunProductionWorkerAsync(string root, Arguments arguments)
    {
        var paths = new SpikePaths(root);
        EnsureDataset(paths);
        string trialId = arguments.Required("trial-id");
        string trialPath = paths.TrialPath(trialId);
        string platform = arguments.Required("platform");
        string representation = arguments.Required("representation");
        bool durable = arguments.Boolean("durable");
        string lifecycle = arguments.Required("lifecycle");
        string runId = arguments.Required("run-id");
        bool warmup = arguments.Boolean("warmup");
        int launch = arguments.Int32("launch");
        int cellOrder = arguments.Int32("cell-order");
        int observation = arguments.Int32("observation");
        int baselineDocs = lifecycle == "fresh" ? 0 : Dataset.BaselineCount;
        int expectedDocs = baselineDocs + Dataset.BatchCount;
        var row = CreateProductionRow(paths, arguments, expectedDocs, baselineDocs);
        string? error = null;
        SpikeObserver? observer = null;

        try
        {
            LeanDocument[] documents = ReadDocuments(paths, Dataset.BatchStart, Dataset.BatchCount).ToArray();
            using var directory = new MMapDirectory(trialPath);
            IndexWriterConfig config = CreateConfig(representation, durable, lifecycle);
            using var writer = new IndexWriter(directory, config);
            if (lifecycle == "reopened-steady")
            {
                row.Set("priming_open_durable", config.DurableCommits);
                row.Set("priming_expected_inherited_file_count", 0);
                row.Set("priming_file_persist_requests", 0);
                row.Set("priming_directory_persist_requests", 0);
                row.Set("priming_durable_baseline_pass", false);
            }
            using var detailedMeasurement = FileSystemDiagnostics.BeginDetailedMeasurement();

            long primingFileRequests = 0;
            long primingDirectoryRequests = 0;
            int? primingGenerationBefore = null;
            int? primingGenerationAfter = null;
            bool? primingPassed = null;
            if (lifecycle != "fresh")
            {
                IndexTopology beforePriming = Topology.Read(trialPath, representation, inspectEveryDocument: false);
                if (!Topology.HasExpectedBaseline(beforePriming))
                    throw new InvalidDataException("The copied reopened baseline does not have the validated nine-segment topology.");

                if (lifecycle == "reopened-steady")
                {
                    int inheritedFileCount = beforePriming.Segments.Sum(static segment => segment.PhysicalFileCount);
                    row.Set("priming_open_durable", config.DurableCommits);
                    row.Set("priming_expected_inherited_file_count", inheritedFileCount);
                    row.Set("priming_file_persist_requests", primingFileRequests);
                    row.Set("priming_directory_persist_requests", primingDirectoryRequests);
                    row.Set("priming_durable_baseline_pass", false);

                    PrimingEvidence priming = EstablishPrimingBaseline(
                        config,
                        durable,
                        beforePriming,
                        FileSystemDiagnostics.GetSnapshot,
                        writer.Commit,
                        () => Topology.Read(trialPath, representation, inspectEveryDocument: false));
                    primingGenerationBefore = priming.GenerationBefore;
                    primingGenerationAfter = priming.GenerationAfter;
                    primingFileRequests = priming.FilePersistRequests;
                    primingDirectoryRequests = priming.DirectoryPersistRequests;
                    primingPassed = priming.Passed;
                    SetPrimingEvidence(row, priming);
                    if (primingPassed != true)
                        throw new InvalidDataException("The unmeasured durable priming commit did not establish the reopened durability baseline.");
                }
            }

            IndexTopology preTopology = Topology.Read(trialPath, representation, inspectEveryDocument: false);
            if (lifecycle == "fresh" && preTopology.SegmentCount != 0
                || lifecycle != "fresh" && !Topology.HasExpectedBaseline(preTopology))
                throw new InvalidDataException("The pre-measurement topology does not match the declared lifecycle.");

            observer = new SpikeObserver();
            observer.Attach();
            FileSystemDiagnosticsSnapshot before = FileSystemDiagnostics.GetSnapshot();
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
            int gen0Before = GC.CollectionCount(0);
            int gen1Before = GC.CollectionCount(1);
            int gen2Before = GC.CollectionCount(2);

            long indexStarted = Stopwatch.GetTimestamp();
            foreach (LeanDocument document in documents)
                writer.AddDocument(document);
            long indexCompleted = Stopwatch.GetTimestamp();

            observer.BeginCommit();
            long commitStarted = Stopwatch.GetTimestamp();
            writer.Commit();
            long commitCompleted = Stopwatch.GetTimestamp();
            observer.Checkpoint("after_commit_return");

            long allocatedAfter = GC.GetTotalAllocatedBytes(precise: false);
            int gen0After = GC.CollectionCount(0);
            int gen1After = GC.CollectionCount(1);
            int gen2After = GC.CollectionCount(2);
            FileSystemDiagnosticsSnapshot after = FileSystemDiagnostics.GetSnapshot();
            SpikeInstrumentation.Observer = null;
            writer.Dispose();
            IndexTopology postTopology = Topology.Read(trialPath, representation, inspectEveryDocument: false);
            bool expectedTopology = HasExpectedMeasuredTopology(postTopology, lifecycle);
            if (!expectedTopology)
                error = AppendError(error, "Post-measurement segment boundaries do not match the declared 10,000-document batch topology.");

            row.Set("index_ms", SpikeInfrastructure.Milliseconds(indexStarted, indexCompleted));
            row.Set("forced_flush_ms", 0d);
            row.Set("forced_flush_embedded_in_commit", true);
            row.Set("compound_pack_ms", observer.PackMilliseconds);
            row.Set("pack_embedded_in_commit", observer.PackEmbeddedInCommit);
            row.Set("metadata_prepare_ms", observer.MetadataPrepareMilliseconds);
            row.Set("durability_sync_ms", observer.DurabilityMilliseconds);
            row.Set("post_commit_ms", observer.DurabilityEndTimestamp == 0
                ? 0d
                : SpikeInfrastructure.Milliseconds(observer.DurabilityEndTimestamp, commitCompleted));
            row.Set("commit_call_ms", SpikeInfrastructure.Milliseconds(commitStarted, commitCompleted));
            row.Set("operation_ms", SpikeInfrastructure.Milliseconds(indexStarted, indexCompleted)
                + SpikeInfrastructure.Milliseconds(commitStarted, commitCompleted));
            row.Set("pack_member_count", observer.PackMemberCount);
            row.Set("pack_member_size_vector_bytes", observer.PackMemberSizeVectorBytes);
            row.Set("pre_measure_segment_count", preTopology.SegmentCount);
            row.Set("pre_measure_segment_doc_vector", preTopology.DocumentVector);
            row.Set("post_measure_segment_count", postTopology.SegmentCount);
            row.Set("post_measure_segment_doc_vector", postTopology.DocumentVector);
            row.Set("priming_commit_generation_before", primingGenerationBefore);
            row.Set("priming_commit_generation_after", primingGenerationAfter);
            row.Set("priming_file_persist_requests", primingFileRequests);
            row.Set("priming_directory_persist_requests", primingDirectoryRequests);
            row.Set("priming_durable_baseline_pass", primingPassed is null ? "not_applicable" : primingPassed.Value);
            row.Set("pack_input_bytes", observer.PackInputBytes);
            row.Set("pack_output_bytes", observer.PackOutputBytes);
            row.Set("pack_source_read_bytes", observer.PackSourceReadBytes);
            row.Set("pack_temp_written_bytes", observer.PackTempWrittenBytes);
            row.Set("pack_temp_explicit_persist_requests", observer.PackTempExplicitPersistRequests);
            row.Set("durability_candidate_files", observer.DurabilityCandidateFiles);
            row.Set("durability_candidate_bytes", observer.DurabilityCandidateBytes);
            row.Set("file_persist_requests", after.FileSyncCount - before.FileSyncCount);
            row.Set("file_persist_success", observer.FilePersistSuccess);
            row.Set("file_persist_failed", observer.FilePersistFailed);
            row.Set("file_persist_elapsed_ms", after.FileSyncElapsedMilliseconds - before.FileSyncElapsedMilliseconds);
            row.Set("directory_persist_requests", observer.DirectoryPersistRequests);
            row.Set("directory_persist_success", observer.DirectoryPersistSuccess);
            row.Set("directory_persist_unsupported", observer.DirectoryPersistUnsupported);
            row.Set("directory_persist_failed", observer.DirectoryPersistFailed);
            row.Set("directory_persist_elapsed_ms", after.DirectorySyncElapsedMilliseconds - before.DirectorySyncElapsedMilliseconds);
            row.Set("atomic_replace_count", observer.AtomicReplaceCount);
            row.Set("commit_marker_persist_requests", observer.CommitMarkerPersistRequests);
            row.Set("files_created", after.FilesCreated - before.FilesCreated);
            row.Set("files_renamed", observer.FilesRenamed);
            row.Set("files_deleted", observer.FilesDeleted);
            row.Set("windows_retry_count", after.RetryCount - before.RetryCount);
            row.Set("windows_retry_delay_ms", after.RetryDelayMilliseconds - before.RetryDelayMilliseconds);
            row.Set("allocated_bytes_delta", allocatedAfter - allocatedBefore);
            row.Set("gen0_delta", gen0After - gen0Before);
            row.Set("gen1_delta", gen1After - gen1Before);
            row.Set("gen2_delta", gen2After - gen2Before);

            ValidationResult validation = ValidateIndex(trialPath, expectedDocs, representation,
                LookupOffsets.Select(offset => Dataset.ReadRecord(paths, Dataset.BatchStart + offset).Id).ToArray());
            row.Set("reopen_ok", validation.ReopenOk);
            row.Set("actual_doc_count", validation.ActualDocumentCount);
            row.Set("id_lookup_pass", validation.IdLookupPass);
            row.Set("index_integrity_pass", validation.IndexIntegrityPass);
            error = AppendError(error, validation.Error);
            if (after.FileSyncCount - before.FileSyncCount != observer.FilePersistRequests)
                error = AppendError(error, "FileSystemDiagnostics FileSyncCount did not match observed file persistence requests.");
            if (observer.PackInputBytes != observer.PackSourceReadBytes)
                error = AppendError(error, "Pack input bytes did not match bytes actually read from loose members.");
            if (observer.PackTempWrittenBytes != observer.PackOutputBytes)
                error = AppendError(error, "Closed compound temporary lengths did not match published compound lengths.");
            if (observer.PackTempExplicitPersistRequests != 0)
                error = AppendError(error, "The production compound temporary output unexpectedly received an explicit persistence request.");

            row.Set("error", error ?? string.Empty);
            row.Append(paths.ProductionTrialsPath);
            if (validation.Pass && string.IsNullOrEmpty(error))
                Directory.Delete(trialPath, recursive: true);
            else
                PreserveFailedTrial(paths, trialPath, trialId);
            return Task.FromResult(validation.Pass && string.IsNullOrEmpty(error) ? 0 : 2);
        }
        catch (Exception exception)
        {
            SpikeInstrumentation.Observer = null;
            error = exception.ToString();
            row.Set("error", error);
            row.Append(paths.ProductionTrialsPath);
            PreserveFailedTrial(paths, trialPath, trialId);
            Console.Error.WriteLine(error);
            return Task.FromResult(1);
        }
    }

    private static IEnumerable<LeanDocument> ReadDocuments(SpikePaths paths, int startOrdinal, int count)
    {
        foreach (SearchRecord record in Dataset.ReadRange(paths, startOrdinal, count))
        {
            var document = new LeanDocument();
            document.Add(new StringField("id", record.Id));
            document.Add(new TextField("body", record.Body, stored: true));
            yield return document;
        }
    }

    private static IndexWriterConfig CreateConfig(string representation, bool durable, string? lifecycle = null)
        => new()
        {
            IndexingConcurrency = 1,
            MaxConcurrentFlushes = 1,
            RamBufferSizeMB = 32,
            MaxBufferedDocs = 10_000,
            MergePolicy = NoMergePolicy.Instance,
            UseCompoundFile = representation == "compound",
            DurableCommits = lifecycle == "reopened-steady" || durable
        };

    private static bool HasExpectedMeasuredTopology(IndexTopology topology, string lifecycle)
    {
        int expectedSegments = lifecycle == "fresh" ? 1 : 10;
        if (topology.SegmentCount != expectedSegments)
            return false;
        if (lifecycle != "fresh" && !Topology.HasExpectedBaseline(new IndexTopology(topology.Segments.Take(9).ToArray())))
            return false;
        SegmentTopology measured = topology.Segments[^1];
        return measured.MinDocumentOrdinal == Dataset.BatchStart
            && measured.MaxDocumentOrdinal == Dataset.BatchStart + Dataset.BatchCount - 1
            && measured.DocumentCount == Dataset.BatchCount;
    }

    private static ValidationResult ValidateIndex(
        string path,
        int expectedDocs,
        string representation,
        IReadOnlyList<string> measuredIds)
    {
        bool reopenOk = false;
        int actualDocs = 0;
        bool idLookupPass = false;
        bool integrityPass = false;
        bool representationPass = false;
        bool noTemporaryFiles = false;
        string? error = null;

        try
        {
            using (var searcher = new IndexSearcher(new MMapDirectory(path)))
            {
                reopenOk = true;
                actualDocs = searcher.Stats.TotalDocCount;
                idLookupPass = measuredIds.Count == 0 || measuredIds.All(id =>
                    searcher.Search(new TermQuery("id", id), 1).TotalHits == 1);
            }

            using var validationDirectory = new MMapDirectory(path);
            IndexCheckResult validation = IndexValidator.Check(validationDirectory, new IndexCheckOptions { Deep = true });
            integrityPass = validation.IsHealthy && validation.FilesChecked > 0;
            if (!integrityPass)
                error = string.Join(" | ", validation.Issues);

            string[] files = Directory.GetFiles(path);
            bool hasCompound = files.Any(static file => file.EndsWith(".cfs", StringComparison.OrdinalIgnoreCase));
            representationPass = representation == "compound" ? hasCompound : !hasCompound;
            noTemporaryFiles = files.All(static file => !Path.GetFileName(file).EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
            if (actualDocs != expectedDocs)
                error = AppendError(error, $"Expected {expectedDocs} documents after reopen, found {actualDocs}.");
            if (!idLookupPass)
                error = AppendError(error, "One or more fixed measured-batch ID lookups failed.");
            if (!representationPass)
                error = AppendError(error, $"Published files do not match the requested {representation} representation.");
            if (!noTemporaryFiles)
                error = AppendError(error, "A temporary publication file remained after successful commit.");
        }
        catch (Exception exception)
        {
            error = AppendError(error, exception.ToString());
        }

        bool pass = reopenOk && actualDocs == expectedDocs && idLookupPass && integrityPass && representationPass && noTemporaryFiles;
        return new ValidationResult(reopenOk, actualDocs, idLookupPass, integrityPass, pass, error ?? string.Empty);
    }

    private static CsvRowBuilder CreateProductionRow(
        SpikePaths paths,
        Arguments arguments,
        int expectedDocs,
        int baselineDocs)
    {
        var row = new CsvRowBuilder(CsvSchemas.Production);
        DataForgeDatasetIdentity identity = Dataset.ReadIdentity(paths);
        row.Set("run_id", arguments.Required("run-id"));
        row.Set("platform_id", arguments.Required("platform"));
        row.Set("experiment_sha", arguments.Required("experiment-sha"));
        row.Set("base_sha", arguments.Required("base-sha"));
        row.Set("launch", arguments.Int32("launch"));
        row.Set("cell_order", arguments.Int32("cell-order"));
        row.Set("observation", arguments.Int32("observation"));
        row.Set("warmup", arguments.Boolean("warmup"));
        row.Set("dataset_id", identity.GetShortKey());
        row.Set("corpus_sha256", identity.ContentSha256);
        row.Set("representation", arguments.Required("representation"));
        row.Set("durable", arguments.Boolean("durable"));
        row.Set("lifecycle", arguments.Required("lifecycle"));
        row.Set("batch_docs", Dataset.BatchCount);
        row.Set("baseline_docs", baselineDocs);
        row.Set("indexing_concurrency", 1);
        row.Set("flush_concurrency", 1);
        row.Set("ram_buffer_mib", 32);
        row.Set("max_buffered_docs", 10_000);
        row.Set("merge_policy", "NoMergePolicy");
        row.Set("priming_open_durable", "not_applicable");
        row.Set("priming_expected_inherited_file_count", null);
        row.Set("priming_commit_generation_before", null);
        row.Set("priming_commit_generation_after", null);
        row.Set("priming_file_persist_requests", 0);
        row.Set("priming_directory_persist_requests", 0);
        row.Set("priming_durable_baseline_pass", "not_applicable");
        row.Set("expected_doc_count", expectedDocs);
        row.Set("reopen_ok", false);
        row.Set("actual_doc_count", null);
        row.Set("id_lookup_pass", false);
        row.Set("index_integrity_pass", false);
        row.Set("error", string.Empty);
        return row;
    }

    private static void EnsureProductionFailureRecorded(
        SpikePaths paths,
        IReadOnlyDictionary<string, string> workerArguments,
        ProcessResult result,
        string logPath)
    {
        int launch = int.Parse(workerArguments["launch"], CultureInfo.InvariantCulture);
        int cellOrder = int.Parse(workerArguments["cell-order"], CultureInfo.InvariantCulture);
        int observation = int.Parse(workerArguments["observation"], CultureInfo.InvariantCulture);
        if (Csv.Read(paths.ProductionTrialsPath).Any(row =>
            row["launch"] == launch.ToString(CultureInfo.InvariantCulture)
            && row["cell_order"] == cellOrder.ToString(CultureInfo.InvariantCulture)
            && row["observation"] == observation.ToString(CultureInfo.InvariantCulture)))
            return;

        var row = new CsvRowBuilder(CsvSchemas.Production);
        DataForgeDatasetIdentity identity = Dataset.ReadIdentity(paths);
        string lifecycle = workerArguments["lifecycle"];
        int baselineDocs = lifecycle == "fresh" ? 0 : Dataset.BaselineCount;
        row.Set("run_id", workerArguments["run-id"]);
        row.Set("platform_id", workerArguments["platform"]);
        row.Set("experiment_sha", workerArguments["experiment-sha"]);
        row.Set("base_sha", workerArguments["base-sha"]);
        row.Set("launch", launch);
        row.Set("cell_order", cellOrder);
        row.Set("observation", observation);
        row.Set("warmup", workerArguments["warmup"]);
        row.Set("dataset_id", identity.GetShortKey());
        row.Set("corpus_sha256", identity.ContentSha256);
        row.Set("representation", workerArguments["representation"]);
        row.Set("durable", workerArguments["durable"]);
        row.Set("lifecycle", lifecycle);
        row.Set("batch_docs", Dataset.BatchCount);
        row.Set("baseline_docs", baselineDocs);
        row.Set("indexing_concurrency", 1);
        row.Set("flush_concurrency", 1);
        row.Set("ram_buffer_mib", 32);
        row.Set("max_buffered_docs", 10_000);
        row.Set("merge_policy", "NoMergePolicy");
        row.Set("expected_doc_count", baselineDocs + Dataset.BatchCount);
        row.Set("reopen_ok", false);
        row.Set("id_lookup_pass", false);
        row.Set("index_integrity_pass", false);
        row.Set("error", $"Worker exited {result.ExitCode}; log={Path.GetFileName(logPath)}; stderr={result.StandardError.Trim()}");
        row.Append(paths.ProductionTrialsPath);
    }

    private static void WriteProductionPairsForLaunch(SpikePaths paths, int launch)
    {
        var rows = Csv.Read(paths.ProductionTrialsPath)
            .Where(row => int.Parse(row["launch"], CultureInfo.InvariantCulture) == launch)
            .ToDictionary(row => (
                int.Parse(row["observation"], CultureInfo.InvariantCulture),
                row["lifecycle"],
                bool.Parse(row["durable"]),
                row["representation"]));
        for (int observation = 0; observation <= 5; observation++)
        foreach (string lifecycle in new[] { "fresh", "reopened-first", "reopened-steady" })
        foreach (bool durable in new[] { false, true })
        {
            rows.TryGetValue((observation, lifecycle, durable, "loose"), out Dictionary<string, string>? loose);
            rows.TryGetValue((observation, lifecycle, durable, "compound"), out Dictionary<string, string>? compound);
            string status = "valid";
            string reason = string.Empty;
            if (loose is null || compound is null)
            {
                status = "missing_observation";
                reason = "One representation has no raw observation row.";
            }
            else if (!string.IsNullOrEmpty(loose["error"]) || !string.IsNullOrEmpty(compound["error"]))
            {
                status = "observation_failure";
                reason = "At least one raw observation recorded an error.";
            }
            else if (NormalizeVector(loose["pre_measure_segment_doc_vector"]) != NormalizeVector(compound["pre_measure_segment_doc_vector"])
                || NormalizeVector(loose["post_measure_segment_doc_vector"]) != NormalizeVector(compound["post_measure_segment_doc_vector"]))
            {
                status = "topology_mismatch";
                reason = "Loose and compound logical document-boundary vectors differ.";
            }

            var pair = new CsvRowBuilder(CsvSchemas.ProductionPairs);
            pair.Set("platform_id", loose?["platform_id"] ?? compound?["platform_id"]);
            pair.Set("launch", launch);
            pair.Set("observation", observation);
            pair.Set("lifecycle", lifecycle);
            pair.Set("durable", durable);
            pair.Set("pair_status", status);
            pair.Set("exclusion_reason", reason);
            pair.Set("loose_pre_vector", loose?["pre_measure_segment_doc_vector"]);
            pair.Set("compound_pre_vector", compound?["pre_measure_segment_doc_vector"]);
            pair.Set("loose_post_vector", loose?["post_measure_segment_doc_vector"]);
            pair.Set("compound_post_vector", compound?["post_measure_segment_doc_vector"]);
            pair.Set("loose_durability_sync_ms", loose?["durability_sync_ms"]);
            pair.Set("compound_durability_sync_ms", compound?["durability_sync_ms"]);
            pair.Set("durability_saving_ms", Difference(loose, compound, "durability_sync_ms"));
            pair.Set("loose_commit_call_ms", loose?["commit_call_ms"]);
            pair.Set("compound_commit_call_ms", compound?["commit_call_ms"]);
            pair.Set("loose_operation_ms", loose?["operation_ms"]);
            pair.Set("compound_operation_ms", compound?["operation_ms"]);
            pair.Set("operation_saving_ms", Difference(loose, compound, "operation_ms"));
            pair.Set("loose_durability_candidate_files", loose?["durability_candidate_files"]);
            pair.Set("compound_durability_candidate_files", compound?["durability_candidate_files"]);
            pair.Set("loose_file_persist_requests", loose?["file_persist_requests"]);
            pair.Set("compound_file_persist_requests", compound?["file_persist_requests"]);
            pair.Append(paths.ProductionPairsPath);
        }
    }

    private static object? Difference(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right,
        string column)
    {
        if (left is null || right is null
            || !double.TryParse(left[column], NumberStyles.Float, CultureInfo.InvariantCulture, out double loose)
            || !double.TryParse(right[column], NumberStyles.Float, CultureInfo.InvariantCulture, out double compound))
            return null;
        return loose - compound;
    }

    private static string NormalizeVector(string vector)
    {
        using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(vector) ? "[]" : vector);
        return JsonSerializer.Serialize(document.RootElement);
    }

    private static List<(int CellOrder, string CellId)> ReadExecutionOrder(string root, int launch)
        => Csv.Read(Path.Combine(root, "execution-order.csv"))
            .Where(row => int.Parse(row["launch"], CultureInfo.InvariantCulture) == launch)
            .OrderBy(row => int.Parse(row["cell_order"], CultureInfo.InvariantCulture))
            .Select(row => (int.Parse(row["cell_order"], CultureInfo.InvariantCulture), row["cell_id"]))
            .ToList();

    private static void EnsureDataset(SpikePaths paths)
    {
        if (!File.Exists(paths.IdentityPath) || !File.Exists(paths.RecordsPath) || !File.Exists(paths.OffsetsPath))
            throw new FileNotFoundException("Run prepare mode to generate the DataForge dataset first.");
    }

    private static string AppendError(string? current, string next)
        => string.IsNullOrEmpty(next) ? current ?? string.Empty
            : string.IsNullOrEmpty(current) ? next
            : current + " | " + next;

    private static void PreserveFailedTrial(SpikePaths paths, string trialPath, string trialId)
    {
        if (!Directory.Exists(trialPath))
            return;
        string destination = Path.Combine(paths.FailedDirectory, trialId);
        if (Directory.Exists(destination))
            destination += "-" + Guid.NewGuid().ToString("N")[..8];
        Directory.Move(trialPath, destination);
    }

    private readonly record struct ValidationResult(
        bool ReopenOk,
        int ActualDocumentCount,
        bool IdLookupPass,
        bool IndexIntegrityPass,
        bool Pass,
        string Error);

    private readonly record struct ProductionMeasurements(
        SpikeObserver Observer,
        FileSystemDiagnosticsSnapshot Before,
        FileSystemDiagnosticsSnapshot After,
        long IndexStarted,
        long IndexCompleted,
        long CommitStarted,
        long CommitCompleted,
        long AllocatedBytesDelta,
        int Gen0Delta,
        int Gen1Delta,
        int Gen2Delta);

    private readonly record struct Cell(string Representation, bool Durable, string Lifecycle)
    {
        public static Cell Parse(string value)
        {
            string? representation = value.StartsWith("loose-", StringComparison.Ordinal) ? "loose"
                : value.StartsWith("compound-", StringComparison.Ordinal) ? "compound"
                : null;
            if (representation is null)
                throw new InvalidDataException($"Invalid production cell ID '{value}'.");

            string remainder = value[(representation.Length + 1)..];
            int separator = remainder.IndexOf('-');
            if (separator < 0)
                throw new InvalidDataException($"Invalid production cell ID '{value}'.");

            string durability = remainder[..separator];
            string lifecycle = remainder[(separator + 1)..];
            if (durability is not ("disabled" or "enabled")
                || lifecycle is not ("fresh" or "reopened-first" or "reopened-steady"))
                throw new InvalidDataException($"Invalid production cell ID '{value}'.");

            return new Cell(representation, durability == "enabled", lifecycle);
        }
    }
}
