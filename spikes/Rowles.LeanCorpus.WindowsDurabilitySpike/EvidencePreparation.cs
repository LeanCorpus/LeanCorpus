using System.Text;
using System.Text.Json;
using Rowles.DataForge.Workloads;
using Rowles.LeanCorpus.WindowsDurabilitySpike.Mechanisms;
using Rowles.LeanCorpus.WindowsDurabilitySpike.Publication;

namespace Rowles.LeanCorpus.WindowsDurabilitySpike;

internal static class EvidencePreparation
{
    internal static int CaptureEnvironment(SpikeArguments arguments)
    {
        string evidence = Path.GetFullPath(arguments.Required("evidence"));
        string environmentClass = arguments.Required("environment-class");
        string dataRoot = Path.GetFullPath(arguments.Required("data-root"));
        string order = Path.GetFullPath(arguments.Required("order"));
        if (Directory.Exists(evidence))
            throw new IOException($"Environment evidence directory already exists and will not be replaced: {evidence}");
        if (!File.Exists(order))
            throw new FileNotFoundException("A predeclared order file is required for environment provenance.", order);
        Directory.CreateDirectory(evidence);
        EnvironmentSnapshot.Write(evidence, environmentClass, dataRoot, order, launch: 0);
        Console.WriteLine("Environment and loaded assembly hashes written to " + evidence);
        return 0;
    }

    internal static int WriteDatasetIdentity(SpikeArguments arguments)
    {
        string output = Path.GetFullPath(arguments.Required("output"));
        string recoveryOutput = arguments.Optional("recovery-records", string.Empty);
        string? recoveryPath = string.IsNullOrWhiteSpace(recoveryOutput)
            ? null
            : Path.GetFullPath(recoveryOutput);
        if (File.Exists(output))
            throw new IOException($"Dataset identity already exists and will not be replaced: {output}");
        if (recoveryPath is not null && File.Exists(recoveryPath))
            throw new IOException($"Recovery dataset already exists and will not be replaced: {recoveryPath}");
        if (recoveryPath is not null && string.Equals(output, recoveryPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Dataset identity and recovery records must use different paths.");

        PublicationRunner.DatasetBatch batch = PublicationRunner.BuildDataset();
        SearchRecord[]? measuredRecords = null;
        if (recoveryPath is not null)
        {
            measuredRecords = batch.Records.Skip(90_000).Take(10_000).ToArray();
            if (measuredRecords.Length != 10_000 || measuredRecords[0].Ordinal != 90_000 ||
                measuredRecords[^1].Ordinal != 99_999)
                throw new InvalidDataException("DataForge did not produce the declared 90,000..99,999 measured range.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(batch.Identity,
            new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        if (recoveryPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(recoveryPath)!);
            File.WriteAllText(recoveryPath, JsonSerializer.Serialize(new RecoveryDatasetFile(
                batch.Identity, 42, 90_000, 99_999, measuredRecords!),
                new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        }
        Console.WriteLine("DataForge dataset identity written to " + output);
        return 0;
    }

    internal static int WriteSourceMap(SpikeArguments arguments)
    {
        string output = Path.GetFullPath(arguments.Required("output"));
        if (File.Exists(output))
            throw new IOException($"Source map already exists and will not be replaced: {output}");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, SourceMap, new UTF8Encoding(false));
        Console.WriteLine("Source and measurement map written to " + output);
        return 0;
    }

    private const string SourceMap = """
        # Spike 2 source and measurement map

        The branch-local instrumentation is internal to Rowles.LeanCorpus. It is active only while the Spike 2 observer is scoped into the current async flow. The candidate-specific publication API is the declared experiment variation.

        | Measurement or boundary | Source | Recorded evidence |
        |---|---|---|
        | Changed-file durability candidates and already available file lengths | `src/core/Rowles.LeanCorpus/Index/Indexer/CommitManager.cs` | Candidate count, bytes, sync-stage duration and semantic checkpoint events |
        | Windows open, flush and safe-handle close phases | `src/core/Rowles.LeanCorpus/Store/WindowsFileSystem.cs` | `CreateFileW`, `FlushFileBuffers`, close timestamps, status and immediate Win32 error |
        | POSIX native call sequence for the Linux neutrality control | `src/core/Rowles.LeanCorpus/Store/PosixFileSystem.cs` | In-memory `open`, `fsync`, `close` probe sequence |
        | Wrapper persistence, retry classification and delay | `src/core/Rowles.LeanCorpus/Store/DirectoryFsync.cs`, `src/core/Rowles.LeanCorpus/Store/PlatformFileSystem.cs`, `src/core/Rowles.LeanCorpus/Store/FileOpenRetry.cs` | Existing operation timing, retry count and retry delay; no extra filesystem query |
        | Temporary marker write, persistence, publication and directory confirmation | `src/core/Rowles.LeanCorpus/Store/IndexAtomicFileWriter.cs` | Existing marker operations, publication call, candidate volume check and checkpoints |
        | P1/P2 same-volume `MoveFileExW` call | `src/core/Rowles.LeanCorpus/Store/DurabilitySpikeInstrumentation.cs` | Same-volume check, flags `0x00000009`, return value and captured Win32 error |
        | Compound temporary close, rename and loose-member deletion | `src/core/Rowles.LeanCorpus/Store/CompoundFile.cs` | Existing pack operation duration and semantic checkpoints |
        | Native A/C/E mechanisms, payload creation and per-file samples | `spikes/Rowles.LeanCorpus.WindowsDurabilitySpike/Mechanisms/MechanismRunner.cs` | Raw observation and per-file CSV files; data is generated before each timed interval |
        | Publication latency and candidate correctness | `spikes/Rowles.LeanCorpus.WindowsDurabilitySpike/Publication/PublicationRunner.cs` | Per-launch publication rows, operation ledger, DataForge identity and candidate validation |
        | Process crash, hard reset recovery inspection and injected failures | `spikes/Rowles.LeanCorpus.WindowsDurabilitySpike/Publication/RecoveryRunner.cs` and `scripts/hard-reset-controller.py` | Recovery rows, flushed control records, read-only inspection, marker inventory and host reset events |
        | Mechanism, hosted and publication classification | `spikes/Rowles.LeanCorpus.WindowsDurabilitySpike/Analysis/` | Deterministic contract fixtures, summaries, leave-one-launch-out results and provenance hashes |
        | WPR collection | `spikes/Rowles.LeanCorpus.WindowsDurabilitySpike/scripts/capture-publication-trace.ps1` | ETL hash plus in-process ordered native event ledger in `trace-index.csv` |

        Instrumentation records timestamps, native results and values already present in the production call path. It does not add file existence checks, stats, directory walks, file reads, hashes, writes, flushes, closes or publication calls to a measured interval solely to create evidence. The deterministic observer-neutrality validation compares enabled and disabled native call order on the executing platform. Windows-native equivalence must be validated on the disposable Windows guest before any measurement.

        P0 follows the existing `File.Move` path. P1/P2 use the branch-local `MoveFileExW` helper with replace-existing and write-through flags, require equal volume roots and do not enable copy/delete fallback. P1 omits the post-publication directory persistence operation; P2 retains it. None adds a final-path marker flush.
        """;

    private sealed record RecoveryDatasetFile(
        Rowles.DataForge.DataForgeDatasetIdentity DatasetIdentity,
        ulong Seed,
        int FirstOrdinal,
        int LastOrdinal,
        SearchRecord[] Records);
}
