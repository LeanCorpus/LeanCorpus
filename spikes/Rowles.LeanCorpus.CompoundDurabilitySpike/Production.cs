using System.Diagnostics;
using System.Globalization;
using Rowles.DataForge.Workloads;
using Rowles.LeanCorpus.Diagnostics;
using Rowles.LeanCorpus.Document;
using Rowles.LeanCorpus.Document.Fields;
using Rowles.LeanCorpus.Index;
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
            foreach (LeanDocument document in ReadDocuments(paths, 0, Dataset.BaselineCount))
                writer.AddDocument(document);
            writer.Commit();
        }

        ValidationResult validation = ValidateIndex(path, Dataset.BaselineCount, representation, []);
        if (!validation.Pass)
            throw new InvalidDataException($"Baseline '{representation}' failed validation: {validation.Error}");

        Console.WriteLine($"baseline={representation} documents={Dataset.BaselineCount} path={path}");
        return Task.FromResult(0);
    }

    public static async Task<int> RunProductionLaunchAsync(string root, Arguments arguments)
    {
        var paths = new SpikePaths(root);
        string platform = arguments.Required("platform");
        int launch = arguments.Int32("launch");
        string runId = File.ReadAllText(Path.Combine(root, "run-id.txt")).Trim();
        string baseSha = File.ReadAllText(Path.Combine(root, "base-sha.txt")).Trim();
        string spikeSha = File.ReadAllText(Path.Combine(root, "spike-sha.txt")).Trim();
        List<(int CellOrder, string CellId)> cells = ReadExecutionOrder(root, launch);

        foreach (var (cellOrder, cellId) in cells)
        {
            Cell cell = Cell.Parse(cellId);
            for (int observation = 0; observation <= 5; observation++)
            {
                bool warmup = observation == 0;
                string trialId = $"prod-l{launch}-c{cellOrder}-o{observation}-{cellId}";
                string trialPath = paths.TrialPath(trialId);
                if (Directory.Exists(trialPath))
                    throw new IOException($"Trial path already exists: {trialId}");
                if (cell.Lifecycle == "reopened")
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
                    ["base-sha"] = baseSha,
                    ["spike-sha"] = spikeSha
                };
                ProcessResult result = await SpikeInfrastructure.RunWorkerAsync(root, logPath, workerArguments);
                if (result.ExitCode != 0)
                {
                    Console.Error.WriteLine($"trial {trialId} exited {result.ExitCode}: {result.StandardError.Trim()}");
                    var recorded = Csv.Read(paths.ProductionTrialsPath).LastOrDefault(row =>
                        row["launch"] == launch.ToString(CultureInfo.InvariantCulture)
                        && row["cell_order"] == cellOrder.ToString(CultureInfo.InvariantCulture)
                        && row["observation"] == observation.ToString(CultureInfo.InvariantCulture));
                    if (recorded is not null && double.TryParse(recorded["commit_call_ms"], NumberStyles.Float,
                            CultureInfo.InvariantCulture, out _)
                        && (recorded["reopen_ok"] != "true" || recorded["actual_doc_count"] != recorded["expected_doc_count"]
                            || recorded["id_lookup_pass"] != "true" || recorded["index_integrity_pass"] != "true"
                            || recorded["error"].Contains("Published files do not match", StringComparison.Ordinal)
                            || recorded["error"].Contains("temporary publication file remained", StringComparison.Ordinal)
                            || recorded["error"].Contains("FileSystemDiagnostics FileSyncCount", StringComparison.Ordinal)
                            || recorded["error"].Contains("Pack input bytes", StringComparison.Ordinal)
                            || recorded["error"].Contains("compound temporary lengths", StringComparison.Ordinal)))
                        throw new InvalidDataException($"Measured commit {trialId} failed its post-commit correctness checks; the platform run stopped.");
                }
            }

            int invalidMeasured = Csv.Read(paths.ProductionTrialsPath)
                .Count(row => row["launch"] == launch.ToString(CultureInfo.InvariantCulture)
                    && row["cell_order"] == cellOrder.ToString(CultureInfo.InvariantCulture)
                    && row["warmup"] == "false"
                    && !string.IsNullOrEmpty(row["error"]));
            if (invalidMeasured > 1)
                throw new InvalidDataException($"Launch {launch} cell {cellId} has {invalidMeasured} invalid measured observations; platform run stopped.");
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
        int baselineDocs = lifecycle == "reopened" ? Dataset.BaselineCount : 0;
        int expectedDocs = baselineDocs + Dataset.BatchCount;
        var row = CreateProductionRow(paths, arguments, expectedDocs, baselineDocs);
        string? error = null;

        try
        {
            LeanDocument[] documents = ReadDocuments(paths, Dataset.BatchStart, Dataset.BatchCount).ToArray();
            ProductionMeasurements measurement = MeasureProductionCommit(trialPath, documents, representation, durable);
            SpikeObserver observer = measurement.Observer;
            FileSystemDiagnosticsSnapshot before = measurement.Before;
            FileSystemDiagnosticsSnapshot after = measurement.After;

            row.Set("index_ms", SpikeInfrastructure.Milliseconds(measurement.IndexStarted, measurement.IndexCompleted));
            row.Set("forced_flush_ms", 0d);
            row.Set("forced_flush_embedded_in_commit", true);
            row.Set("compound_pack_ms", observer.PackMilliseconds);
            row.Set("pack_embedded_in_commit", observer.PackEmbeddedInCommit);
            row.Set("metadata_prepare_ms", observer.MetadataPrepareMilliseconds);
            row.Set("durability_sync_ms", observer.DurabilityMilliseconds);
            row.Set("post_commit_ms", observer.DurabilityEndTimestamp == 0
                ? 0d
                : SpikeInfrastructure.Milliseconds(observer.DurabilityEndTimestamp, measurement.CommitCompleted));
            row.Set("commit_call_ms", SpikeInfrastructure.Milliseconds(measurement.CommitStarted, measurement.CommitCompleted));
            row.Set("operation_ms", SpikeInfrastructure.Milliseconds(measurement.IndexStarted, measurement.IndexCompleted)
                + SpikeInfrastructure.Milliseconds(measurement.CommitStarted, measurement.CommitCompleted));
            row.Set("pack_member_count", observer.PackMemberCount);
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
            row.Set("allocated_bytes_delta", measurement.AllocatedBytesDelta);
            row.Set("gen0_delta", measurement.Gen0Delta);
            row.Set("gen1_delta", measurement.Gen1Delta);
            row.Set("gen2_delta", measurement.Gen2Delta);

            ValidationResult validation = ValidateIndex(trialPath, expectedDocs, representation,
                LookupOffsets.Select(offset => Dataset.ReadRecord(paths, Dataset.BatchStart + offset).Id).ToArray());
            row.Set("reopen_ok", validation.ReopenOk);
            row.Set("actual_doc_count", validation.ActualDocumentCount);
            row.Set("id_lookup_pass", validation.IdLookupPass);
            row.Set("index_integrity_pass", validation.IndexIntegrityPass);
            error = validation.Error;
            if (Convert.ToInt64(after.FileSyncCount - before.FileSyncCount, CultureInfo.InvariantCulture) != observer.FilePersistRequests)
                error = AppendError(error, "FileSystemDiagnostics FileSyncCount did not match observed file persistence requests.");
            if (observer.PackInputBytes != observer.PackSourceReadBytes)
                error = AppendError(error, "Pack input bytes did not match bytes actually read from loose members.");
            if (observer.PackTempWrittenBytes != observer.PackOutputBytes)
                error = AppendError(error, "Closed compound temporary lengths did not match published compound lengths.");

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

    private static IndexWriterConfig CreateConfig(string representation, bool durable)
        => new()
        {
            IndexingConcurrency = 1,
            MaxConcurrentFlushes = 1,
            RamBufferSizeMB = 32,
            MergePolicy = NoMergePolicy.Instance,
            UseCompoundFile = representation == "compound",
            DurableCommits = durable
        };

    private static ProductionMeasurements MeasureProductionCommit(
        string trialPath,
        IReadOnlyList<LeanDocument> documents,
        string representation,
        bool durable)
    {
        using var directory = new MMapDirectory(trialPath);
        using var writer = new IndexWriter(directory, CreateConfig(representation, durable));
        using var detailedMeasurement = FileSystemDiagnostics.BeginDetailedMeasurement();
        var observer = new SpikeObserver(
            Environment.GetEnvironmentVariable("SPIKE_FAILPOINT"),
            Environment.GetEnvironmentVariable("SPIKE_CONTROL_LOG"));
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
        return new ProductionMeasurements(
            observer,
            before,
            after,
            indexStarted,
            indexCompleted,
            commitStarted,
            commitCompleted,
            allocatedAfter - allocatedBefore,
            gen0After - gen0Before,
            gen1After - gen1Before,
            gen2After - gen2Before);
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
        var identity = Dataset.ReadIdentity(paths);
        row.Set("run_id", arguments.Required("run-id"));
        row.Set("platform_id", arguments.Required("platform"));
        row.Set("launch", arguments.Int32("launch"));
        row.Set("cell_order", arguments.Int32("cell-order"));
        row.Set("observation", arguments.Int32("observation"));
        row.Set("warmup", arguments.Boolean("warmup"));
        row.Set("base_sha", arguments.Required("base-sha"));
        row.Set("spike_sha", arguments.Required("spike-sha"));
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
        row.Set("merge_policy", "NoMergePolicy");
        row.Set("expected_doc_count", expectedDocs);
        row.Set("reopen_ok", false);
        row.Set("actual_doc_count", null);
        row.Set("id_lookup_pass", false);
        row.Set("index_integrity_pass", false);
        row.Set("error", string.Empty);
        return row;
    }

    private static List<(int CellOrder, string CellId)> ReadExecutionOrder(string root, int launch)
        => File.ReadLines(Path.Combine(root, "execution-order.csv"))
            .Skip(1)
            .Select(static line => line.Split(','))
            .Where(fields => int.Parse(fields[0], CultureInfo.InvariantCulture) == launch)
            .Select(fields => (int.Parse(fields[1], CultureInfo.InvariantCulture), fields[2]))
            .OrderBy(static item => item.Item1)
            .Select(static item => (item.Item1, item.Item2))
            .ToList();

    private static void EnsureDataset(SpikePaths paths)
    {
        if (!File.Exists(paths.IdentityPath) || !File.Exists(paths.RecordsPath) || !File.Exists(paths.OffsetsPath))
            throw new FileNotFoundException("Run the spike prepare mode to generate the frozen DataForge dataset first.");
    }

    private static string AppendError(string? current, string next)
        => string.IsNullOrEmpty(current) ? next : current + " | " + next;

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
            string[] parts = value.Split('-');
            if (parts.Length != 3 || parts[0] is not ("loose" or "compound")
                || parts[1] is not ("disabled" or "enabled") || parts[2] is not ("fresh" or "reopened"))
                throw new InvalidDataException($"Invalid production cell ID '{value}'.");
            return new Cell(parts[0], parts[1] == "enabled", parts[2]);
        }
    }
}
