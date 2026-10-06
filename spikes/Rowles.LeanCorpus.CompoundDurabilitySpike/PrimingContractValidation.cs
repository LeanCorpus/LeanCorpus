using Rowles.LeanCorpus.Diagnostics;
using Rowles.LeanCorpus.Index.Indexer;

namespace Rowles.LeanCorpus.CompoundDurabilitySpike;

internal sealed record PrimingEvidence(
    bool OpenDurable,
    int ExpectedInheritedFileCount,
    int? GenerationBefore,
    int? GenerationAfter,
    long FilePersistRequests,
    long DirectoryPersistRequests,
    bool Passed);

internal static partial class SpikeRunner
{
    private static PrimingEvidence EstablishPrimingBaseline(
        IndexWriterConfig config,
        bool measuredDurable,
        IndexTopology beforeTopology,
        Func<FileSystemDiagnosticsSnapshot> captureDiagnostics,
        Action commit,
        Func<IndexTopology> readAfterTopology)
    {
        bool openDurable = config.DurableCommits;
        int inheritedFileCount = beforeTopology.Segments.Sum(static segment => segment.PhysicalFileCount);
        FileSystemDiagnosticsSnapshot before = captureDiagnostics();
        commit();
        FileSystemDiagnosticsSnapshot after = captureDiagnostics();
        IndexTopology afterTopology = readAfterTopology();
        long fileRequests = after.FileSyncCount - before.FileSyncCount;
        long directoryRequests = after.DirectorySyncAttemptCount - before.DirectorySyncAttemptCount;
        bool passed = PrimingBaselinePasses(
            beforeTopology,
            afterTopology,
            openDurable,
            inheritedFileCount,
            fileRequests,
            directoryRequests);

        if (passed)
            config.DurableCommits = measuredDurable;

        return new PrimingEvidence(
            openDurable,
            inheritedFileCount,
            beforeTopology.CommitGeneration,
            afterTopology.CommitGeneration,
            fileRequests,
            directoryRequests,
            passed);
    }

    private static bool PrimingBaselinePasses(
        IndexTopology before,
        IndexTopology after,
        bool openDurable,
        int inheritedFileCount,
        long fileRequests,
        long directoryRequests)
        => Topology.HasExpectedBaseline(before)
            && before.CommitGeneration is int generationBefore
            && after.CommitGeneration == generationBefore + 1
            && Topology.HasExpectedBaseline(after)
            && SameLogicalTopology(before, after)
            && openDurable
            && inheritedFileCount > 0
            && fileRequests >= inheritedFileCount
            && directoryRequests > 0;

    private static void SetPrimingEvidence(CsvRowBuilder row, PrimingEvidence evidence)
    {
        row.Set("priming_open_durable", evidence.OpenDurable);
        row.Set("priming_expected_inherited_file_count", evidence.ExpectedInheritedFileCount);
        row.Set("priming_commit_generation_before", evidence.GenerationBefore);
        row.Set("priming_commit_generation_after", evidence.GenerationAfter);
        row.Set("priming_file_persist_requests", evidence.FilePersistRequests);
        row.Set("priming_directory_persist_requests", evidence.DirectoryPersistRequests);
        row.Set("priming_durable_baseline_pass", evidence.Passed);
    }

    public static Task<int> ValidatePrimingContractAsync(string root, Arguments arguments)
    {
        IndexWriterConfig steadyNonDurable = CreateConfig("loose", durable: false, lifecycle: "reopened-steady");
        if (!steadyNonDurable.DurableCommits
            || CreateConfig("loose", durable: false, lifecycle: "fresh").DurableCommits
            || CreateConfig("loose", durable: false, lifecycle: "reopened-first").DurableCommits
            || !CreateConfig("loose", durable: true, lifecycle: "reopened-steady").DurableCommits)
            throw new InvalidDataException("Writer-open durability does not match the lifecycle contract.");

        IndexTopology beforeTopology = CreateContractTopology(generation: 12);
        IndexTopology afterTopology = CreateContractTopology(generation: 13);
        int snapshotIndex = 0;
        bool commitObservedDurableOpen = false;
        PrimingEvidence evidence = EstablishPrimingBaseline(
            steadyNonDurable,
            measuredDurable: false,
            beforeTopology,
            () => snapshotIndex++ == 0
                ? DiagnosticsSnapshot(fileSyncCount: 100, directorySyncCount: 20)
                : DiagnosticsSnapshot(fileSyncCount: 100 + beforeTopology.Segments.Sum(static segment => (long)segment.PhysicalFileCount) + 3,
                    directorySyncCount: 21),
            () => commitObservedDurableOpen = steadyNonDurable.DurableCommits,
            () => afterTopology);

        if (!commitObservedDurableOpen || !evidence.Passed || steadyNonDurable.DurableCommits)
            throw new InvalidDataException("The priming commit did not run durably before the non-durable measured phase.");

        string validationDirectory = Path.Combine(root, "priming-contract-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(validationDirectory);
        string rawPath = Path.Combine(validationDirectory, "production-trial.csv");
        try
        {
            Csv.Create(rawPath, CsvSchemas.Production);
            var row = new CsvRowBuilder(CsvSchemas.Production);
            SetPrimingEvidence(row, evidence);
            row.Append(rawPath);

            Dictionary<string, string> emitted = Csv.Read(rawPath).Single();
            int expectedFiles = beforeTopology.Segments.Sum(static segment => segment.PhysicalFileCount);
            long emittedFileRequests = long.Parse(emitted["priming_file_persist_requests"], System.Globalization.CultureInfo.InvariantCulture);
            if (!IsSteadyPrimingObservationValid(emitted)
                || emitted["priming_open_durable"] != "true"
                || emitted["priming_expected_inherited_file_count"] != expectedFiles.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || emittedFileRequests < expectedFiles
                || emitted["priming_directory_persist_requests"] != "1"
                || emitted["priming_durable_baseline_pass"] != "true")
                throw new InvalidDataException("The emitted priming CSV fields did not prove the inherited-file persistence floor.");

            var belowFloor = new Dictionary<string, string>(emitted, StringComparer.Ordinal)
            {
                ["priming_file_persist_requests"] = (expectedFiles - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            if (IsSteadyPrimingObservationValid(belowFloor))
                throw new InvalidDataException("Priming validation accepted fewer file requests than the inherited physical-file count.");

            var nonDurableOpen = new Dictionary<string, string>(emitted, StringComparer.Ordinal)
            {
                ["priming_open_durable"] = "false"
            };
            if (IsSteadyPrimingObservationValid(nonDurableOpen))
                throw new InvalidDataException("Priming validation accepted a writer opened without durability.");

            var noDirectoryRequest = new Dictionary<string, string>(emitted, StringComparer.Ordinal)
            {
                ["priming_directory_persist_requests"] = "0"
            };
            if (IsSteadyPrimingObservationValid(noDirectoryRequest))
                throw new InvalidDataException("Priming validation accepted a priming commit with no directory request.");

            Console.WriteLine($"priming-contract-validation=passed inherited_files={expectedFiles} file_requests={emittedFileRequests} measured_durable={steadyNonDurable.DurableCommits.ToString().ToLowerInvariant()}");
            return Task.FromResult(0);
        }
        finally
        {
            if (Directory.Exists(validationDirectory))
                Directory.Delete(validationDirectory, recursive: true);
        }
    }

    private static IndexTopology CreateContractTopology(int generation)
        => new(Enumerable.Range(0, 9).Select(index => new SegmentTopology(
            index,
            $"seg_{index}",
            index * 10_000,
            index * 10_000 + 9_999,
            10_000,
            index + 1,
            100_000L + index)).ToArray(), generation);

    private static FileSystemDiagnosticsSnapshot DiagnosticsSnapshot(long fileSyncCount, long directorySyncCount)
        => new(0, 0, 0, 0, 0)
        {
            FileSyncCount = fileSyncCount,
            DirectorySyncAttemptCount = directorySyncCount
        };
}
