using System.Diagnostics;
using System.Globalization;
using Rowles.LeanCorpus.Diagnostics;
using Rowles.LeanCorpus.Store;

namespace Rowles.LeanCorpus.CompoundDurabilitySpike;

internal static partial class SpikeRunner
{
    private static readonly int[] CardinalityCounts = [1, 2, 4, 8, 16, 32, 64];
    private static readonly byte[] MarkerBytes = Enumerable.Range(0, 4096).Select(static value => (byte)value).ToArray();

    public static async Task<int> RunCardinalityLaunchAsync(string root, Arguments arguments)
    {
        var paths = new SpikePaths(root);
        int launch = arguments.Int32("launch");
        string platform = arguments.Required("platform");
        string runId = File.ReadAllText(Path.Combine(root, "run-id.txt")).Trim();
        string baseSha = File.ReadAllText(Path.Combine(root, "base-sha.txt")).Trim();
        string spikeSha = File.ReadAllText(Path.Combine(root, "spike-sha.txt")).Trim();
        List<(int CellOrder, int ObjectCount, bool Durable)> cells = ReadCardinalityOrder(root, launch);

        foreach (var (cellOrder, objectCount, durable) in cells)
        {
            // Five measured observations per cell per launch give fifteen across three launches.
            for (int observation = 0; observation <= 5; observation++)
            {
                bool warmup = observation == 0;
                string trialId = $"card-l{launch}-c{cellOrder}-o{observation}-k{objectCount}-{durable}";
                var workerArguments = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["mode"] = "cardinality-worker",
                    ["root"] = root,
                    ["platform"] = platform,
                    ["launch"] = launch.ToString(CultureInfo.InvariantCulture),
                    ["cell-order"] = cellOrder.ToString(CultureInfo.InvariantCulture),
                    ["observation"] = observation.ToString(CultureInfo.InvariantCulture),
                    ["warmup"] = warmup.ToString().ToLowerInvariant(),
                    ["trial-id"] = trialId,
                    ["object-count"] = objectCount.ToString(CultureInfo.InvariantCulture),
                    ["durable"] = durable.ToString().ToLowerInvariant(),
                    ["run-id"] = runId,
                    ["base-sha"] = baseSha,
                    ["spike-sha"] = spikeSha
                };
                ProcessResult result = await SpikeInfrastructure.RunWorkerAsync(
                    root,
                    Path.Combine(paths.LogsDirectory, trialId + ".log"),
                    workerArguments);
                if (result.ExitCode != 0)
                {
                    Console.Error.WriteLine($"cardinality trial {trialId} exited {result.ExitCode}: {result.StandardError.Trim()}");
                    var recorded = Csv.Read(paths.CardinalityTrialsPath).LastOrDefault(row =>
                        row["launch"] == launch.ToString(CultureInfo.InvariantCulture)
                        && row["cell_order"] == cellOrder.ToString(CultureInfo.InvariantCulture)
                        && row["observation"] == observation.ToString(CultureInfo.InvariantCulture));
                    if (recorded is not null && recorded["payload_reconstruction_pass"] != "true")
                        throw new InvalidDataException($"Cardinality payload reconstruction failed in {trialId}; the platform run stopped.");
                }
            }
        }
        return 0;
    }

    public static Task<int> RunCardinalityWorkerAsync(string root, Arguments arguments)
    {
        var paths = new SpikePaths(root);
        string trialId = arguments.Required("trial-id");
        int objectCount = arguments.Int32("object-count");
        if (!CardinalityCounts.Contains(objectCount))
            throw new ArgumentOutOfRangeException(nameof(objectCount));
        bool durable = arguments.Boolean("durable");
        int launch = arguments.Int32("launch");
        int cellOrder = arguments.Int32("cell-order");
        int observation = arguments.Int32("observation");
        bool warmup = arguments.Boolean("warmup");
        string trialPath = paths.TrialPath(trialId);
        Directory.CreateDirectory(trialPath);
        var row = new CsvRowBuilder(CsvSchemas.Cardinality);
        string? error = null;

        try
        {
            byte[] canonicalPayload = File.ReadAllBytes(paths.PayloadPath);
            string expectedSha = File.ReadAllText(paths.PayloadShaPath).Trim();
            if (canonicalPayload.Length != Dataset.PayloadBytes || Dataset.HashFile(paths.PayloadPath) != expectedSha)
                throw new InvalidDataException("The frozen cardinality payload does not match its recorded length or SHA-256.");

            string[] filePaths = PartitionPayload(canonicalPayload, objectCount, trialPath);
            using var detailedMeasurement = FileSystemDiagnostics.BeginDetailedMeasurement();
            var observer = new SpikeObserver();
            observer.Attach();
            FileSystemDiagnosticsSnapshot before = FileSystemDiagnostics.GetSnapshot();
            long started = Stopwatch.GetTimestamp();
            foreach (string filePath in filePaths)
            {
                if (durable)
                    DirectoryFsync.SyncFile(filePath, strict: true);
            }
            if (durable)
                DirectoryFsync.Sync(trialPath, strict: true);
            IndexAtomicFileWriter.Write(
                Path.Combine(trialPath, "publication.marker"),
                durable,
                syncDirectory: true,
                stream => stream.Write(MarkerBytes));
            long completed = Stopwatch.GetTimestamp();
            FileSystemDiagnosticsSnapshot after = FileSystemDiagnostics.GetSnapshot();
            bool reconstructionPass = VerifyPayload(canonicalPayload, filePaths);

            row.Set("run_id", arguments.Required("run-id"));
            row.Set("platform_id", arguments.Required("platform"));
            row.Set("launch", launch);
            row.Set("cell_order", cellOrder);
            row.Set("observation", observation);
            row.Set("warmup", warmup);
            row.Set("base_sha", arguments.Required("base-sha"));
            row.Set("spike_sha", arguments.Required("spike-sha"));
            row.Set("object_count", objectCount);
            row.Set("payload_bytes", canonicalPayload.Length);
            row.Set("payload_sha256", expectedSha);
            row.Set("durable", durable);
            row.Set("durability_sync_ms", SpikeInfrastructure.Milliseconds(started, completed));
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
            row.Set("marker_bytes", MarkerBytes.Length);
            row.Set("payload_reconstruction_pass", reconstructionPass);
            if (!reconstructionPass)
                error = "Payload partitions did not reproduce the canonical 64 MiB payload byte-for-byte.";
            long filePersistRequests = after.FileSyncCount - before.FileSyncCount;
            long expectedFilePersistRequests = durable ? objectCount + 1L : 0L;
            if (filePersistRequests != observer.FilePersistRequests)
                error = AppendError(error, "FileSystemDiagnostics FileSyncCount did not match observed file persistence requests.");
            if (filePersistRequests != expectedFilePersistRequests)
                error = AppendError(error, $"Expected {expectedFilePersistRequests} explicit file persistence requests; observed {filePersistRequests}.");
            long expectedDirectoryRequests = durable ? 2L : 0L;
            if (observer.DirectoryPersistRequests != expectedDirectoryRequests)
                error = AppendError(error, $"Expected {expectedDirectoryRequests} parent-directory persistence requests; observed {observer.DirectoryPersistRequests}.");
            if (observer.AtomicReplaceCount != 1)
                error = AppendError(error, $"Expected one atomic marker replacement; observed {observer.AtomicReplaceCount}.");
            row.Set("error", error ?? string.Empty);
            row.Append(paths.CardinalityTrialsPath);
            if (string.IsNullOrEmpty(error))
                Directory.Delete(trialPath, recursive: true);
            else
                PreserveFailedTrial(paths, trialPath, trialId);
            return Task.FromResult(string.IsNullOrEmpty(error) ? 0 : 2);
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            PopulateFailedCardinalityRow(row, arguments, objectCount, trialId, paths, error);
            row.Append(paths.CardinalityTrialsPath);
            PreserveFailedTrial(paths, trialPath, trialId);
            Console.Error.WriteLine(error);
            return Task.FromResult(1);
        }
    }

    private static string[] PartitionPayload(byte[] payload, int objectCount, string directory)
    {
        int baseLength = payload.Length / objectCount;
        string[] paths = new string[objectCount];
        int offset = 0;
        for (int index = 0; index < objectCount; index++)
        {
            int length = index == objectCount - 1 ? payload.Length - offset : baseLength;
            string path = Path.Combine(directory, $"payload-{index:D3}.bin");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       1024 * 1024, FileOptions.SequentialScan))
            {
                stream.Write(payload.AsSpan(offset, length));
            }
            paths[index] = path;
            offset += length;
        }
        return paths;
    }

    private static bool VerifyPayload(byte[] expected, IReadOnlyList<string> filePaths)
    {
        int offset = 0;
        byte[] buffer = new byte[1024 * 1024];
        foreach (string path in filePaths)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                buffer.Length, FileOptions.SequentialScan);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (offset > expected.Length - read || !buffer.AsSpan(0, read).SequenceEqual(expected.AsSpan(offset, read)))
                    return false;
                offset += read;
            }
        }
        return offset == expected.Length;
    }

    private static List<(int CellOrder, int ObjectCount, bool Durable)> ReadCardinalityOrder(string root, int launch)
        => File.ReadLines(Path.Combine(root, "cardinality-order.csv"))
            .Skip(1)
            .Select(static line => line.Split(','))
            .Where(fields => int.Parse(fields[0], CultureInfo.InvariantCulture) == launch)
            .Select(fields =>
            {
                string[] parts = fields[2].Split('-');
                return (int.Parse(fields[1], CultureInfo.InvariantCulture), int.Parse(parts[0], CultureInfo.InvariantCulture), parts[1] == "enabled");
            })
            .OrderBy(static item => item.Item1)
            .Select(static item => (item.Item1, item.Item2, item.Item3))
            .ToList();

    private static void PopulateFailedCardinalityRow(
        CsvRowBuilder row,
        Arguments arguments,
        int objectCount,
        string trialId,
        SpikePaths paths,
        string error)
    {
        string expectedSha = File.Exists(paths.PayloadShaPath) ? File.ReadAllText(paths.PayloadShaPath).Trim() : string.Empty;
        row.Set("run_id", arguments.Required("run-id"));
        row.Set("platform_id", arguments.Required("platform"));
        row.Set("launch", arguments.Int32("launch"));
        row.Set("cell_order", arguments.Int32("cell-order"));
        row.Set("observation", arguments.Int32("observation"));
        row.Set("warmup", arguments.Boolean("warmup"));
        row.Set("base_sha", arguments.Required("base-sha"));
        row.Set("spike_sha", arguments.Required("spike-sha"));
        row.Set("object_count", objectCount);
        row.Set("payload_bytes", Dataset.PayloadBytes);
        row.Set("payload_sha256", expectedSha);
        row.Set("durable", arguments.Boolean("durable"));
        row.Set("durability_sync_ms", 0d);
        row.Set("file_persist_requests", 0);
        row.Set("file_persist_success", 0);
        row.Set("file_persist_failed", 0);
        row.Set("file_persist_elapsed_ms", 0d);
        row.Set("directory_persist_requests", 0);
        row.Set("directory_persist_success", 0);
        row.Set("directory_persist_unsupported", 0);
        row.Set("directory_persist_failed", 0);
        row.Set("directory_persist_elapsed_ms", 0d);
        row.Set("atomic_replace_count", 0);
        row.Set("marker_bytes", MarkerBytes.Length);
        row.Set("payload_reconstruction_pass", false);
        row.Set("error", error);
    }
}
