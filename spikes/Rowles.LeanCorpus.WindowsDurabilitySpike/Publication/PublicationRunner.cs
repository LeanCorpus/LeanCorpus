using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Rowles.DataForge;
using Rowles.DataForge.Workloads;
using Rowles.LeanCorpus.Document;
using Rowles.LeanCorpus.Document.Fields;
using Rowles.LeanCorpus.Index;
using Rowles.LeanCorpus.Index.Indexer;
using Rowles.LeanCorpus.Search.Queries;
using Rowles.LeanCorpus.Search.Scoring;
using Rowles.LeanCorpus.Search.Searcher;
using Rowles.LeanCorpus.Store;
using Rowles.LeanCorpus.WindowsDurabilitySpike.Analysis;
using Rowles.LeanCorpus.WindowsDurabilitySpike.Mechanisms;

namespace Rowles.LeanCorpus.WindowsDurabilitySpike.Publication;

internal static class PublicationRunner
{
    private static readonly string[] Candidates = ["P0", "P1", "P2"];

    internal static int PrepareOrder(SpikeArguments arguments)
    {
        string output = Path.GetFullPath(arguments.Required("output"));
        var rows = new List<PublicationOrderRow>();
        for (int launch = 1; launch <= 5; launch++)
        {
            uint seed = (uint)(20261400 + launch);
            var cells = (from candidate in Candidates
                         from representation in new[] { "loose", "compound" }
                         select new PublicationCell(candidate, representation)).ToList();
            SeededShuffle.Shuffle(cells, seed);
            int order = 0;
            var launchRows = new List<PublicationOrderRow>(36);
            foreach (PublicationCell cell in cells)
            for (int observation = 0; observation <= 5; observation++)
                launchRows.Add(new PublicationOrderRow(launch, seed, 0, cell.Candidate,
                    cell.Representation, observation, observation == 0));
            SeededShuffle.Shuffle(launchRows, seed ^ 0x85EBCA6Bu);
            foreach (PublicationOrderRow row in launchRows)
                rows.Add(row with { Order = order++ });
        }

        using var csv = new CsvFile(output,
            "launch_index", "execution_order_seed", "order_index", "candidate_id",
            "representation", "observation_index", "warm_up");
        foreach (PublicationOrderRow row in rows)
            csv.WriteRow(row.Launch, row.Seed, row.Order, row.Candidate,
                row.Representation, row.Observation, row.WarmUp);
        Console.WriteLine($"Wrote {rows.Count} publication attempts to {output}");
        return 0;
    }

    internal static int ValidateCandidates(SpikeArguments arguments)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Publication candidates require Windows validation.");
        string root = Path.GetFullPath(arguments.Required("data-root"));
        string output = Path.GetFullPath(arguments.Required("output"));
        if (File.Exists(output))
            throw new IOException($"Candidate validation evidence already exists and will not be replaced: {output}");
        var results = new List<CandidateValidation>();
        var cleanup = new List<string>();
        foreach (string candidate in Candidates)
        foreach (string representation in new[] { "loose", "compound" })
        {
            string trial = Path.Combine(root, $"spike2-candidate-validation-{candidate}-{representation}");
            if (Directory.Exists(trial))
                throw new IOException($"Candidate validation directory already exists: {trial}");
            Directory.CreateDirectory(trial);
            cleanup.Add(trial);
            var observer = new PublicationObserver(candidate);
            string destination = Path.Combine(trial, "existing-marker");
            string temporary = Path.Combine(trial, "replacement-marker.tmp");
            File.WriteAllText(destination, "old", Encoding.UTF8);
            File.WriteAllText(temporary, "new", Encoding.UTF8);
            using (DurabilitySpikeInstrumentation.Begin(observer))
                DurabilitySpikeInstrumentation.PublishMarker(temporary, destination, overwrite: true);
            bool replacementPassed = File.ReadAllText(destination, Encoding.UTF8) == "new" && !File.Exists(temporary);
            if (!replacementPassed)
                throw new InvalidOperationException($"{candidate} did not replace an existing marker at {representation}.");

            string indexPath = Path.Combine(trial, "index");
            BuildSmallIndex(indexPath, candidate, representation == "compound", observer);
            var recovery = IndexRecovery.RecoverLatestCommit(indexPath, cleanupOrphans: false)
                ?? throw new InvalidDataException($"{candidate} {representation} produced no recoverable commit.");
            using var directory = new MMapDirectory(indexPath);
            IndexCheckResult validation = IndexValidator.Check(directory, new IndexCheckOptions { Deep = true });
            if (!validation.IsHealthy)
                throw new InvalidDataException($"{candidate} {representation} failed deep validation: {string.Join("; ", validation.Issues)}");
            using var searchDirectory = new MMapDirectory(indexPath);
            using var searcher = new IndexSearcher(searchDirectory);
            int documentCount = searcher.Search(new MatchAllDocsQuery(), 1).TotalHits;
            int lookupCount = searcher.Search(new TermQuery("id", "spike-00000099"), 1).TotalHits;
            if (documentCount != 101 || lookupCount != 1)
                throw new InvalidDataException($"{candidate} {representation} recovered {documentCount} documents and {lookupCount} fixed-ID hits.");
            bool compoundPresent = Directory.EnumerateFiles(indexPath, "*.cfs").Any();
            if (compoundPresent != (representation == "compound"))
                throw new InvalidDataException($"Expected representation '{representation}', compound present={compoundPresent}.");

            results.Add(new CandidateValidation(candidate, representation, true, true,
                recovery.Generation, documentCount, lookupCount, compoundPresent, "passed"));
        }

        var result = new { schema_version = 1, passed = true, tested_at_utc = DateTimeOffset.UtcNow, candidates = results };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        foreach (string trial in cleanup)
            Directory.Delete(trial, recursive: true);
        Console.WriteLine("P0, P1 and P2 passed existing-marker replacement, recovery, search and deep validation for loose and compound indices.");
        return 0;
    }

    internal static int RunLaunch(SpikeArguments arguments)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Publication latency cells require Windows.");
        string dataRoot = Path.GetFullPath(arguments.Required("data-root"));
        string evidence = Path.GetFullPath(arguments.Required("evidence"));
        string orderPath = Path.GetFullPath(arguments.Required("order"));
        string candidateValidation = Path.GetFullPath(arguments.Required("candidate-validation"));
        string semantics = Path.GetFullPath(arguments.Required("semantics"));
        ObservationNeutrality.Require(Path.GetFullPath(arguments.Required("neutrality-validation")));
        int launch = arguments.RequiredInt("launch");
        if (launch is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(launch));
        RequireCandidateGate(candidateValidation);
        if (!File.Exists(semantics) || !File.ReadAllText(semantics).Contains("MOVEFILE_WRITE_THROUGH", StringComparison.Ordinal))
            throw new InvalidOperationException("The publication-semantics.md evidence gate is missing or incomplete.");
        EnsureEvidenceOutsideDataRoot(dataRoot, evidence);

        List<PublicationOrderRow> orderRows = ReadOrder(orderPath).Where(row => row.Launch == launch)
            .OrderBy(static row => row.Order).ToList();
        if (orderRows.Count != 36)
            throw new InvalidDataException($"Launch {launch} requires 36 planned observations, found {orderRows.Count}.");
        string launchEvidence = Path.Combine(evidence, $"launch-{launch}");
        if (Directory.Exists(launchEvidence))
            throw new IOException($"Launch evidence already exists and will not be replaced: {launchEvidence}");
        Directory.CreateDirectory(launchEvidence);
        EnvironmentSnapshot.Write(launchEvidence, "local_windows_vm", dataRoot, orderPath, launch);

        DatasetBatch batch = BuildDataset();
        File.WriteAllText(Path.Combine(launchEvidence, "dataset-identity.json"),
            JsonSerializer.Serialize(batch.Identity, new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));

        var results = new List<PublicationResult>(orderRows.Count);
        var eventRows = new List<PublicationEventRow>();
        var cleanup = new List<string>();
        foreach (PublicationOrderRow order in orderRows)
        {
            string trialPath = Path.Combine(dataRoot,
                $"spike2-publication-launch{launch}-{order.Candidate}-{order.Representation}-o{order.Observation}");
            var result = new PublicationResult(order, trialPath, batch.Identity.ContentSha256);
            results.Add(result);
            try
            {
                if (Directory.Exists(trialPath))
                    throw new IOException($"Publication trial already exists; observations are never replaced: {trialPath}");
                Directory.CreateDirectory(trialPath);
                var observer = new PublicationObserver(order.Candidate);
                using (var directory = new MMapDirectory(trialPath))
                using (var writer = new IndexWriter(directory, CreateConfig(order.Representation == "compound")))
                {
                    AddBatch(writer, batch.Records.Skip(90_000).Take(10_000).ToArray());
                    using (DurabilitySpikeInstrumentation.Begin(observer))
                    {
                        long commitStartedAt = Stopwatch.GetTimestamp();
                        bool commitSucceeded = false;
                        Exception? commitException = null;
                        long commitCompletedAt;
                        try
                        {
                            writer.Commit();
                            commitSucceeded = true;
                        }
                        catch (Exception exception)
                        {
                            commitException = exception;
                        }
                        finally
                        {
                            commitCompletedAt = Stopwatch.GetTimestamp();
                        }
                        if (commitSucceeded)
                            DurabilitySpikeInstrumentation.Checkpoint(DurabilitySpikeCheckpoint.AfterCommitReturn, trialPath);
                        result.CommitCallMs = TicksToMs(commitCompletedAt - commitStartedAt);
                        result.CommitSucceeded = commitSucceeded;
                        result.Error = commitException is null
                            ? null
                            : $"{commitException.GetType().Name}: {commitException.Message}";
                        FillMetrics(result, observer, commitStartedAt, commitCompletedAt);
                    }
                }
                if (result.CommitSucceeded)
                    cleanup.Add(trialPath);
            }
            catch (Exception exception)
            {
                result.Error = $"{exception.GetType().Name}: {exception.Message}";
                result.CommitSucceeded = false;
            }
            eventRows.AddRange(result.Events);
        }

        WritePublicationRows(Path.Combine(launchEvidence, "publication-trials.csv"), results);
        WriteEventRows(Path.Combine(launchEvidence, "publication-events.csv"), eventRows);
        File.WriteAllText(Path.Combine(launchEvidence, "analysis-provenance.json"),
            JsonSerializer.Serialize(new
            {
                experiment_sha = Environment.GetEnvironmentVariable("SPIKE_EXPERIMENT_SHA"),
                launch_index = launch,
                execution_order_seed = orderRows[0].Seed,
                dataset_identity_sha256 = batch.Identity.GetShortKey(),
                candidate_validation_sha256 = HashFile(candidateValidation),
                publication_semantics_sha256 = HashFile(semantics),
                raw_trials_sha256 = HashFile(Path.Combine(launchEvidence, "publication-trials.csv")),
                raw_events_sha256 = HashFile(Path.Combine(launchEvidence, "publication-events.csv"))
            }, new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        foreach (string trialPath in cleanup)
            Directory.Delete(trialPath, recursive: true);

        int failed = results.Count(static result => !result.CommitSucceeded);
        Console.WriteLine($"Publication launch {launch}: {results.Count} attempts, {failed} failed. Evidence: {launchEvidence}");
        return failed == 0 ? 0 : 1;
    }

    internal static int WriteSemantics(SpikeArguments arguments)
    {
        string output = Path.GetFullPath(arguments.Required("output"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, PublicationSemanticsText, new UTF8Encoding(false));
        Console.WriteLine("Wrote publication API semantics and retrieval date to " + output);
        return 0;
    }

    internal static int TraceCell(SpikeArguments arguments)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Publication ETW traces require Windows.");
        string candidate = arguments.Required("candidate");
        string representation = arguments.Required("representation");
        if (!Candidates.Contains(candidate, StringComparer.Ordinal) || representation is not "loose" and not "compound")
            throw new ArgumentException("Trace cells require one declared candidate and representation.");
        string dataRoot = Path.GetFullPath(arguments.Required("data-root"));
        string evidence = Path.GetFullPath(arguments.Required("evidence"));
        string candidateValidation = Path.GetFullPath(arguments.Required("candidate-validation"));
        string semantics = Path.GetFullPath(arguments.Required("semantics"));
        ObservationNeutrality.Require(Path.GetFullPath(arguments.Required("neutrality-validation")));
        RequireCandidateGate(candidateValidation);
        if (!File.Exists(semantics))
            throw new FileNotFoundException("Publication semantic evidence is required before trace capture.", semantics);
        EnsureEvidenceOutsideDataRoot(dataRoot, evidence);
        string cellId = $"{candidate}-{representation}";
        if (Directory.Exists(evidence))
            throw new IOException($"Trace evidence directory already exists and will not be replaced: {evidence}");
        Directory.CreateDirectory(evidence);
        if (Directory.Exists(dataRoot))
            throw new IOException($"Trace data directory already exists and will not be reused: {dataRoot}");
        Directory.CreateDirectory(dataRoot);
        string orderPath = Path.Combine(evidence, "trace-order.csv");
        using (var order = new CsvFile(orderPath, "launch_index", "execution_order_seed", "order_index", "candidate_id",
                   "representation", "observation_index", "warm_up"))
            order.WriteRow(0, 0, 0, candidate, representation, 1, false);
        EnvironmentSnapshot.Write(evidence, "local_windows_vm", dataRoot, orderPath, 0);

        DatasetBatch batch = BuildDataset();
        SearchRecord[] measuredRecords = batch.Records.Skip(90_000).Take(10_000).ToArray();
        if (measuredRecords.Length != 10_000)
            throw new InvalidDataException("Trace workload does not contain the declared 90,000..99,999 measured range.");
        string trialPath = Path.Combine(dataRoot, $"spike2-trace-{cellId}");
        if (Directory.Exists(trialPath))
            throw new IOException($"Trace index already exists and will not be reused: {trialPath}");
        Directory.CreateDirectory(trialPath);
        var observer = new PublicationObserver(candidate);
        var orderRow = new PublicationOrderRow(0, 0, 0, candidate, representation, 1, false);
        var result = new PublicationResult(orderRow, trialPath, batch.Identity.ContentSha256);
        using (var directory = new MMapDirectory(trialPath))
        using (var writer = new IndexWriter(directory, CreateConfig(representation == "compound")))
        {
            AddBatch(writer, measuredRecords);
            long startedAt = Stopwatch.GetTimestamp();
            using (DurabilitySpikeInstrumentation.Begin(observer))
            {
                writer.Commit();
                long completedAt = Stopwatch.GetTimestamp();
                result.CommitSucceeded = true;
                result.CommitCallMs = TicksToMs(completedAt - startedAt);
                FillMetrics(result, observer, startedAt, completedAt);
            }
        }

        DurabilitySpikeEvent[] events = observer.Events.OrderBy(static item => item.StartedAt).ToArray();
        DurabilitySpikeEvent[] markerFlush = events.Where(static item => item.Operation == DurabilitySpikeOperation.MarkerFlush).ToArray();
        DurabilitySpikeEvent[] publish = events.Where(static item => item.Operation == DurabilitySpikeOperation.MarkerPublicationApiCall).ToArray();
        DurabilitySpikeEvent[] postDirectory = events.Where(static item => item.Operation == DurabilitySpikeOperation.PostPublicationDirectorySync).ToArray();
        bool expectedPostDirectory = candidate is "P0" or "P2";
        bool orderPassed = markerFlush.Length == 1 && publish.Length == 1 &&
                           (expectedPostDirectory ? postDirectory.Length == 1 && publish[0].CompletedAt <= postDirectory[0].StartedAt : postDirectory.Length == 0) &&
                           markerFlush[0].CompletedAt <= publish[0].StartedAt && result.CommitSucceeded &&
                           (candidate == "P0" || observer.SameVolumeCheck == "same_volume");
        string sequence = expectedPostDirectory
            ? "temporary_marker_file_persist -> publication_api -> directory_persist_attempt"
            : "temporary_marker_file_persist -> publication_api -> no_postpublication_directory_persist";
        WriteEventRows(Path.Combine(evidence, "trace-cell-events.csv"), result.Events);
        File.WriteAllText(Path.Combine(evidence, "trace-cell.json"),
            JsonSerializer.Serialize(new
            {
                schema_version = 1,
                cell_id = cellId,
                candidate_id = candidate,
                representation,
                expected_create_file_count = events.Count(static item => item.Operation == DurabilitySpikeOperation.WindowsOpen),
                expected_flush_filebuffers_count = events.Count(static item => item.Operation == DurabilitySpikeOperation.WindowsFlush),
                observed_call_sequence_summary = sequence,
                event_order_passed = orderPassed,
                publication_api = result.PublicationApi,
                publication_flags = result.PublicationFlags,
                same_volume_check = result.SameVolumeCheck,
                marker_flush_count = markerFlush.Length,
                publication_call_count = publish.Length,
                postpublication_directory_persist_count = postDirectory.Length,
                directory_persist_outcome = events.Where(static item => item.Operation == DurabilitySpikeOperation.DirectoryPersist)
                    .Select(static item => item.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
                index_directory = trialPath,
                experiment_sha = Environment.GetEnvironmentVariable("SPIKE_EXPERIMENT_SHA") ?? "unknown",
                generated_utc = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        Directory.Delete(trialPath, recursive: true);
        Console.WriteLine($"Trace cell {cellId}: instrumented ordering {(orderPassed ? "passed" : "failed")}.");
        return orderPassed ? 0 : 1;
    }

    private static void BuildSmallIndex(string path, string candidate, bool compound, PublicationObserver observer)
    {
        using var directory = new MMapDirectory(path);
        var config = CreateConfig(compound);
        using (var writer = new IndexWriter(directory, config))
        {
            for (int index = 0; index < 100; index++)
                writer.AddDocument(CreateDocument($"spike-{index:D8}", $"validation document number {index}"));
            using (DurabilitySpikeInstrumentation.Begin(observer))
                writer.Commit();

            writer.AddDocument(CreateDocument("spike-00000100", "replacement generation validation"));
            using (DurabilitySpikeInstrumentation.Begin(observer))
                writer.Commit();
        }
    }

    internal static IndexWriterConfig CreateConfig(bool compound) => new()
    {
        IndexingConcurrency = 1,
        MaxConcurrentFlushes = 1,
        MaxBufferedDocs = 10_000,
        RamBufferSizeMB = 32,
        MaxConcurrentMerges = 1,
        MergePolicy = NoMergePolicy.Instance,
        UseCompoundFile = compound,
        DurableCommits = true
    };

    internal static DatasetBatch BuildDataset(int recordCount = 100_000)
    {
        var profile = new LeanCorpusSearchProfile();
        var options = new DataForgeGenerationOptions(42, recordCount);
        SearchRecord[] records = profile.Generate(options).ToArray();
        using var canonical = new CanonicalJsonWriter(Stream.Null);
        foreach (SearchRecord record in records)
        {
            profile.CanonicalRecordWriter.Write(canonical, record);
            canonical.WriteLine();
        }
        string contentSha256 = Convert.ToHexString(canonical.GetSha256()).ToLowerInvariant();
        var identity = new DataForgeDatasetIdentity(
            DataForgeSourceKind.Generated,
            DataForgeVersions.DataForgeVersion,
            profile.Descriptor.ProfileId,
            profile.Descriptor.ProfileVersion,
            null,
            null,
            options.Seed,
            options.RecordCount,
            options.Parameters,
            profile.Dependencies,
            contentSha256);
        return new DatasetBatch(records, identity);
    }

    internal static void AddBatch(IndexWriter writer, IReadOnlyList<SearchRecord> records)
    {
        for (int index = 0; index < records.Count; index++)
        {
            SearchRecord record = records[index];
            writer.AddDocument(CreateDocument(record.Id, record.Body));
        }
    }

    private static LeanDocument CreateDocument(string id, string body)
    {
        var document = new LeanDocument();
        document.Add(new StringField("id", id, stored: false, boost: 1.0f, storeDocValues: false,
            indexOptions: FieldIndexOptions.DocsOnly));
        document.Add(new TextField("body", body, stored: false, boost: 1.0f,
            indexOptions: FieldIndexOptions.DocsAndFreqs));
        return document;
    }

    private static void FillMetrics(PublicationResult result, PublicationObserver observer, long commitStartedAt, long commitCompletedAt)
    {
        DurabilitySpikeEvent[] events = observer.Events;
        result.Events = events.Select(value => new PublicationEventRow(result.Order.Launch,
            result.Order.Candidate, result.Order.Representation, result.Order.Observation,
            value.Operation.ToString(), value.Path ?? string.Empty, value.StartedAt, value.CompletedAt,
            value.ErrorCode, value.Succeeded, value.Value, value.AuxiliaryValue)).ToArray();
        DurabilitySpikeEvent[] apiCalls = events.Where(static value => value.Operation == DurabilitySpikeOperation.MarkerPublicationApiCall).ToArray();
        DurabilitySpikeEvent[] filePersists = events.Where(static value => value.Operation == DurabilitySpikeOperation.FilePersist).ToArray();
        DurabilitySpikeEvent[] directoryPersists = events.Where(static value => value.Operation == DurabilitySpikeOperation.DirectoryPersist).ToArray();
        DurabilitySpikeEvent[] postDirectory = events.Where(static value => value.Operation == DurabilitySpikeOperation.PostPublicationDirectorySync).ToArray();
        DurabilitySpikeEvent[] markerFlush = events.Where(static value => value.Operation == DurabilitySpikeOperation.MarkerFlush).ToArray();
        DurabilitySpikeEvent[] markerPublication = events.Where(static value => value.Operation == DurabilitySpikeOperation.MarkerPublication).ToArray();
        DurabilitySpikeEvent[] candidateFiles = events.Where(static value => value.Operation == DurabilitySpikeOperation.DurabilityCandidateFile).ToArray();
        DurabilitySpikeEvent[] candidateBytes = events.Where(static value => value.Operation == DurabilitySpikeOperation.DurabilityCandidateBytes).ToArray();
        DurabilitySpikeEvent[] retries = events.Where(static value => value.Operation == DurabilitySpikeOperation.RetryDelay).ToArray();
        DurabilitySpikeEvent[] compoundCopies = events.Where(static value => value.Operation == DurabilitySpikeOperation.CompoundCopy).ToArray();
        DurabilitySpikeEvent[] compoundClose = events.Where(static value => value.Operation == DurabilitySpikeOperation.CompoundClose).ToArray();
        DurabilitySpikeEvent[] compoundPack = events.Where(static value => value.Operation == DurabilitySpikeOperation.CompoundPack).ToArray();

        result.PublicationApi = result.Order.Candidate == "P0" ? "File.Move" : "MoveFileExW";
        result.PublicationFlags = result.Order.Candidate == "P0" ? "not_applicable" : "0x00000009";
        result.SameVolumeCheck = result.Order.Candidate == "P0" ? "same_parent_directory" : observer.SameVolumeCheck;
        result.SourceVolumeRoot = observer.SourceVolumeRoot ?? "not_applicable";
        result.DestinationVolumeRoot = observer.DestinationVolumeRoot ?? "not_applicable";
        result.MoveFileExReturned = result.Order.Candidate == "P0" ? "not_applicable" : observer.MoveFileExReturned?.ToString().ToLowerInvariant() ?? "false";
        result.MoveFileExError = result.Order.Candidate == "P0" ? "not_applicable" : observer.MoveFileExError?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0";
        result.PublicationCallMs = apiCalls.Sum(value => TicksToMs(value.CompletedAt - value.StartedAt));
        result.AtomicReplaceCount = apiCalls.Length;
        result.TempMarkerPersistMs = markerFlush.Sum(value => TicksToMs(value.CompletedAt - value.StartedAt));
        result.DirectoryPersistMs = result.Order.Candidate == "P1"
            ? 0
            : postDirectory.Sum(value => TicksToMs(value.CompletedAt - value.StartedAt));
        result.DirectoryPersistNotApplicable = result.Order.Candidate == "P1";
        result.PublicationToConfirmationMs = CalculatePublicationConfirmation(events, result.Order.Candidate);
        result.FilePersistRequests = filePersists.Length;
        result.FilePersistSuccess = filePersists.Count(static value => value.Succeeded);
        result.FilePersistFailed = filePersists.Count(static value => !value.Succeeded);
        result.FilePersistElapsedMs = filePersists.Sum(value => TicksToMs(value.CompletedAt - value.StartedAt));
        result.DirectoryPersistRequests = directoryPersists.Length;
        result.DirectoryPersistSuccess = directoryPersists.Count(static value => (int)value.Value == 1);
        result.DirectoryPersistUnsupported = directoryPersists.Count(static value => (int)value.Value is 2 or 3);
        result.DirectoryPersistFailed = directoryPersists.Count(static value => (int)value.Value == 0);
        result.DirectoryPersistElapsedMs = directoryPersists.Sum(value => TicksToMs(value.CompletedAt - value.StartedAt));
        result.RetryCount = retries.Length;
        result.RetryDelayMs = retries.Sum(static value => value.Value);
        result.DurabilityCandidateFiles = candidateFiles.Sum(static value => value.Value);
        result.DurabilityCandidateBytes = candidateBytes.Sum(static value => value.Value);
        result.CompoundPackMemberCount = compoundCopies.Length;
        result.CompoundPackInputBytes = compoundCopies.Sum(static value => value.Value);
        result.CompoundPackOutputBytes = compoundClose.Sum(static value => value.Value);
        result.CompoundPackMs = compoundPack.Sum(value => TicksToMs(value.CompletedAt - value.StartedAt));
        DurabilitySpikeEvent? firstPersist = events.Where(static value => value.Operation is DurabilitySpikeOperation.FilePersist or DurabilitySpikeOperation.DirectoryPersist)
            .OrderBy(static value => value.StartedAt).Cast<DurabilitySpikeEvent?>().FirstOrDefault();
        long confirmation = result.Order.Candidate == "P1"
            ? apiCalls.LastOrDefault().CompletedAt
            : postDirectory.LastOrDefault().CompletedAt;
        result.DurabilitySyncMs = firstPersist is null || confirmation == 0
            ? double.NaN
            : TicksToMs(confirmation - firstPersist.Value.StartedAt);
        result.CommitCallMs = result.CommitCallMs == 0 ? TicksToMs(commitCompletedAt - commitStartedAt) : result.CommitCallMs;
    }

    private static double CalculatePublicationConfirmation(IReadOnlyList<DurabilitySpikeEvent> events, string candidate)
    {
        DurabilitySpikeEvent? apiCall = events.Where(static value => value.Operation == DurabilitySpikeOperation.MarkerPublicationApiCall)
            .Cast<DurabilitySpikeEvent?>().FirstOrDefault();
        if (apiCall is null)
            return double.NaN;
        long confirmation = candidate == "P1"
            ? events.LastOrDefault(static value => value.Operation == DurabilitySpikeOperation.MarkerPublicationApiCall && value.Succeeded).CompletedAt
            : events.LastOrDefault(static value => value.Operation == DurabilitySpikeOperation.PostPublicationDirectorySync).CompletedAt;
        return confirmation == 0 ? double.NaN : TicksToMs(confirmation - apiCall.Value.StartedAt);
    }

    private static void WritePublicationRows(string path, IReadOnlyList<PublicationResult> results)
    {
        using var csv = new CsvFile(path,
            "launch_index", "execution_order_seed", "order_index", "candidate_id", "representation",
            "observation_index", "warm_up", "dataset_identity_sha256", "commit_succeeded", "commit_call_ms",
            "durability_sync_ms", "temp_marker_persist_ms", "publication_api", "publication_flags",
            "same_volume_check", "source_volume_root", "destination_volume_root", "publication_call_ms",
            "publication_to_confirmation_ms", "directory_persist_ms",
            "directory_persist_not_applicable", "movefileex_returned", "movefileex_error", "file_persist_requests",
            "file_persist_success", "file_persist_failed", "file_persist_elapsed_ms", "directory_persist_requests",
            "directory_persist_success", "directory_persist_unsupported", "directory_persist_failed",
            "directory_persist_elapsed_ms", "atomic_replace_count", "windows_retry_count", "windows_retry_delay_ms",
            "durability_candidate_files", "durability_candidate_bytes", "compound_pack_member_count",
            "compound_pack_input_bytes", "compound_pack_output_bytes", "compound_pack_ms", "error");
        foreach (PublicationResult value in results)
            csv.WriteRow(value.Order.Launch, value.Order.Seed, value.Order.Order, value.Order.Candidate,
                value.Order.Representation, value.Order.Observation, value.Order.WarmUp,
                value.DatasetSha256, value.CommitSucceeded, value.CommitCallMs, value.DurabilitySyncMs,
                value.TempMarkerPersistMs, value.PublicationApi, value.PublicationFlags, value.SameVolumeCheck,
                value.SourceVolumeRoot, value.DestinationVolumeRoot, value.PublicationCallMs,
                value.PublicationToConfirmationMs, value.DirectoryPersistMs,
                value.DirectoryPersistNotApplicable, value.MoveFileExReturned, value.MoveFileExError,
                value.FilePersistRequests, value.FilePersistSuccess, value.FilePersistFailed,
                value.FilePersistElapsedMs, value.DirectoryPersistRequests, value.DirectoryPersistSuccess,
                value.DirectoryPersistUnsupported, value.DirectoryPersistFailed, value.DirectoryPersistElapsedMs,
                value.AtomicReplaceCount, value.RetryCount, value.RetryDelayMs, value.DurabilityCandidateFiles,
                value.DurabilityCandidateBytes, value.CompoundPackMemberCount, value.CompoundPackInputBytes,
                value.CompoundPackOutputBytes, value.CompoundPackMs, value.Error ?? string.Empty);
    }

    private static void WriteEventRows(string path, IReadOnlyList<PublicationEventRow> events)
    {
        using var csv = new CsvFile(path,
            "launch_index", "candidate_id", "representation", "observation_index", "operation", "path",
            "started_at", "completed_at", "error_code", "succeeded", "value", "auxiliary_value");
        foreach (PublicationEventRow value in events)
            csv.WriteRow(value.Launch, value.Candidate, value.Representation, value.Observation,
                value.Operation, value.Path, value.StartedAt, value.CompletedAt, value.ErrorCode,
                value.Succeeded, value.Value, value.AuxiliaryValue);
    }

    private static List<PublicationOrderRow> ReadOrder(string path)
        => CsvReader.Read(path).Select(row => new PublicationOrderRow(
            int.Parse(row[0]), uint.Parse(row[1]), int.Parse(row[2]), row[3], row[4],
            int.Parse(row[5]), bool.Parse(row[6]))).ToList();

    private static void RequireCandidateGate(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException("Run validate-publication-candidates before any 2B latency measurement.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("passed", out JsonElement passed) || !passed.GetBoolean())
            throw new InvalidOperationException("The publication candidate correctness gate did not pass.");
    }

    private static void EnsureEvidenceOutsideDataRoot(string dataRoot, string evidence)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)) + Path.DirectorySeparatorChar;
        string result = Path.GetFullPath(evidence) + Path.DirectorySeparatorChar;
        if (result.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Evidence must be outside the measured data root.");
    }

    private static double TicksToMs(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record PublicationCell(string Candidate, string Representation);
    private sealed record PublicationOrderRow(int Launch, uint Seed, int Order, string Candidate, string Representation, int Observation, bool WarmUp);
    internal sealed record DatasetBatch(SearchRecord[] Records, DataForgeDatasetIdentity Identity);
    private sealed record CandidateValidation(string Candidate, string Representation, bool MarkerReplacementPassed,
        bool CommitRecoveryPassed, int Generation, int DocumentCount, int FixedIdHits, bool CompoundFilePresent, string Result);

    private sealed class PublicationResult(PublicationOrderRow order, string trialPath, string datasetSha256)
    {
        internal PublicationOrderRow Order { get; } = order;
        internal string TrialPath { get; } = trialPath;
        internal string DatasetSha256 { get; } = datasetSha256;
        internal bool CommitSucceeded { get; set; }
        internal double CommitCallMs { get; set; }
        internal double DurabilitySyncMs { get; set; }
        internal double TempMarkerPersistMs { get; set; }
        internal string PublicationApi { get; set; } = "unknown";
        internal string PublicationFlags { get; set; } = "unknown";
        internal string SameVolumeCheck { get; set; } = "unknown";
        internal string SourceVolumeRoot { get; set; } = "not_applicable";
        internal string DestinationVolumeRoot { get; set; } = "not_applicable";
        internal double PublicationCallMs { get; set; }
        internal double PublicationToConfirmationMs { get; set; }
        internal double DirectoryPersistMs { get; set; }
        internal bool DirectoryPersistNotApplicable { get; set; }
        internal string MoveFileExReturned { get; set; } = "not_applicable";
        internal string MoveFileExError { get; set; } = "not_applicable";
        internal int FilePersistRequests { get; set; }
        internal int FilePersistSuccess { get; set; }
        internal int FilePersistFailed { get; set; }
        internal double FilePersistElapsedMs { get; set; }
        internal int DirectoryPersistRequests { get; set; }
        internal int DirectoryPersistSuccess { get; set; }
        internal int DirectoryPersistUnsupported { get; set; }
        internal int DirectoryPersistFailed { get; set; }
        internal double DirectoryPersistElapsedMs { get; set; }
        internal int AtomicReplaceCount { get; set; }
        internal int RetryCount { get; set; }
        internal double RetryDelayMs { get; set; }
        internal long DurabilityCandidateFiles { get; set; }
        internal long DurabilityCandidateBytes { get; set; }
        internal int CompoundPackMemberCount { get; set; }
        internal long CompoundPackInputBytes { get; set; }
        internal long CompoundPackOutputBytes { get; set; }
        internal double CompoundPackMs { get; set; }
        internal string? Error { get; set; }
        internal IReadOnlyList<PublicationEventRow> Events { get; set; } = [];
    }

    private sealed record PublicationEventRow(int Launch, string Candidate, string Representation,
        int Observation, string Operation, string Path, long StartedAt, long CompletedAt,
        int ErrorCode, bool Succeeded, long Value, int AuxiliaryValue);

    internal sealed class PublicationObserver(
        string candidate,
        Action<DurabilitySpikeCheckpoint, string?>? checkpointAction = null,
        Action<string, bool>? operationHook = null) : IDurabilitySpikeObserver
    {
        private readonly List<DurabilitySpikeEvent> _events = new(512);
        private readonly List<DurabilitySpikeCheckpoint> _checkpoints = new(16);
        private readonly object _gate = new();
        private readonly string _candidate = candidate;

        internal DurabilitySpikeEvent[] Events
        {
            get { lock (_gate) return _events.ToArray(); }
        }

        internal string SameVolumeCheck { get; private set; } = "not_checked";
        internal string? SourceVolumeRoot { get; private set; }
        internal string? DestinationVolumeRoot { get; private set; }
        internal bool? MoveFileExReturned { get; private set; }
        internal int? MoveFileExError { get; private set; }
        public bool SyncDirectoryAfterMarkerPublication => _candidate is "P0" or "P2";

        public void OnEvent(in DurabilitySpikeEvent value)
        {
            lock (_gate)
                _events.Add(value);
        }

        public void OnCheckpoint(DurabilitySpikeCheckpoint checkpoint, string? path)
        {
            lock (_gate)
                _checkpoints.Add(checkpoint);
            checkpointAction?.Invoke(checkpoint, path);
        }

        public void BeforeDurabilityOperation(string operationId)
            => operationHook?.Invoke(operationId, true);

        public void AfterDurabilityOperation(string operationId)
            => operationHook?.Invoke(operationId, false);

        public DirtyFileTracker.DirtyFile PublishMarker(string temporaryPath, string destinationPath, bool overwrite)
        {
            if (_candidate == "P0")
                return FileOpenRetry.Move(temporaryPath, destinationPath, overwrite);
            if (_candidate is not "P1" and not "P2")
                throw new InvalidOperationException($"Unknown publication candidate '{_candidate}'.");
            if (!overwrite)
                throw new InvalidOperationException("The publication experiment always replaces the marker.");
            EnsureSameVolume(temporaryPath, destinationPath);
            const uint flags = 0x00000001 | 0x00000008;
            const int maxRetries = 5;
            int retries = 0;
            while (true)
            {
                long startedAt = Stopwatch.GetTimestamp();
                bool moved = MoveFileExW(temporaryPath, destinationPath, flags);
                int error = moved ? 0 : Marshal.GetLastPInvokeError();
                long completedAt = Stopwatch.GetTimestamp();
                MoveFileExReturned = moved;
                MoveFileExError = error;
                OnEvent(new DurabilitySpikeEvent(DurabilitySpikeOperation.MarkerPublicationApiCall,
                    destinationPath, startedAt, completedAt, error, moved, flags));
                if (moved)
                {
                    AfterDurabilityOperation("publication_call");
                    return DirtyFileTracker.Move(temporaryPath, destinationPath);
                }
                if (retries >= maxRetries || error is not (32 or 33 or 303))
                    throw new System.ComponentModel.Win32Exception(error,
                        $"MoveFileExW failed for '{destinationPath}'.");

                retries++;
                Diagnostics.FileSystemDiagnostics.RecordRetry(200);
                long delayStartedAt = Stopwatch.GetTimestamp();
                Thread.Sleep(200);
                OnEvent(new DurabilitySpikeEvent(DurabilitySpikeOperation.RetryDelay,
                    destinationPath, delayStartedAt, Stopwatch.GetTimestamp(), value: 200));
            }
        }

        private void EnsureSameVolume(string source, string destination)
        {
            SourceVolumeRoot = GetVolumeRoot(source);
            DestinationVolumeRoot = GetVolumeRoot(destination);
            bool same = string.Equals(SourceVolumeRoot, DestinationVolumeRoot, StringComparison.OrdinalIgnoreCase);
            SameVolumeCheck = same ? "same_volume" : "cross_volume_publication";
            if (!same)
                throw new InvalidOperationException("cross_volume_publication");
        }

        private string GetVolumeRoot(string path)
        {
            var buffer = new StringBuilder(32768);
            long startedAt = Stopwatch.GetTimestamp();
            bool success = NativeVolumeInformation.GetVolumePathName(path, buffer, (uint)buffer.Capacity);
            int error = success ? 0 : Marshal.GetLastPInvokeError();
            long completedAt = Stopwatch.GetTimestamp();
            OnEvent(new DurabilitySpikeEvent(DurabilitySpikeOperation.CandidateVolumeCheck,
                path, startedAt, completedAt, error, success));
            if (!success)
            {
                throw new System.ComponentModel.Win32Exception(error, "GetVolumePathNameW failed.");
            }
            return buffer.ToString();
        }

        [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileExW(string existingFileName, string newFileName, uint flags);
    }

    private const string PublicationSemanticsText = """
# Publication API semantics

Retrieved 2026-10-07 from Microsoft Learn.

## P0: .NET File.Move

The .NET `File.Move` API moves a file and accepts an overwrite option. Microsoft documents cross-volume moves as copy-and-delete behaviour. The experiment creates the temporary commit marker beside the destination, so P0 uses a same-directory move. The API documentation does not state a crash-atomicity or power-loss durability guarantee for that same-volume operation. P0 retains the existing `FileOpenRetry.Move` call and its bounded transient retry policy.

Source: [File.Move method](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.move)

## P1 and P2: MoveFileExW

`MOVEFILE_REPLACE_EXISTING` permits replacement of the destination. `MOVEFILE_WRITE_THROUGH` requests that the function not return until the move is performed on disk; the documentation describes a flush at the end of a copy-and-delete move. This spike requires same-volume publication and does not enable `MOVEFILE_COPY_ALLOWED`, so the copy-and-delete description does not establish a documented same-volume rename-metadata durability guarantee. P1 remains an empirical recovery candidate. P2 adds the existing directory-persistence attempt after the move.

The branch passes exactly `MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH` (`0x00000009`) with Unicode marshalling and captures `GetLastError` immediately after a false return. Source and destination volume roots are resolved first and must compare equal case-insensitively. Copy/delete fallback is not enabled.

Source: [MoveFileExW function](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw)

## Existing Windows file persistence

The current file persistence path opens an existing file with `CreateFileW`, `GENERIC_WRITE`, and read/write/delete sharing, then calls `FlushFileBuffers` and disposes the handle. Microsoft documents `FlushFileBuffers` as flushing buffered information for the specified file; the documentation does not establish universal physical-power-loss survival through every filesystem, controller, hypervisor and device cache.

Source: [FlushFileBuffers function](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-flushfilebuffers)

## Existing directory persistence

The current implementation opens the directory with `CreateFileW` and `FILE_FLAG_BACKUP_SEMANTICS`, then calls `FlushFileBuffers`. Microsoft documents the backup-semantics flag as allowing a directory handle to be opened. The `FlushFileBuffers` documentation does not list directory handles as a general supported target. The current implementation treats Windows `ERROR_ACCESS_DENIED` as an unsupported per-volume directory-flush result, caches that capability state, and reports subsequent requests as skipped unsupported; other strict errors propagate. A successful call is recorded as an observed success on this tested stack only.

Sources: [CreateFileW directory handles](https://learn.microsoft.com/en-us/windows/win32/fileio/obtaining-a-handle-to-a-directory), [FlushFileBuffers function](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-flushfilebuffers)

## Claim boundary

These API references do not document the complete same-volume P0/P1/P2 sequence as a crash-atomic and physical-power-loss contract. Empirical VM reset survival is scoped to the tested guest, hypervisor, host storage and cache configuration. Process termination is reported separately. A strict documented-contract claim is not made unless a cited authoritative source covers the complete same-volume sequence.
""";
}

internal static class PublicationSemantics
{
    internal static int Write(SpikeArguments arguments) => PublicationRunner.WriteSemantics(arguments);
}

internal static class PublicationCandidateValidation
{
    internal static int Run(SpikeArguments arguments) => PublicationRunner.ValidateCandidates(arguments);
}
