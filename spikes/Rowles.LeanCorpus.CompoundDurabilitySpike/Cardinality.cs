using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rowles.LeanCorpus.Diagnostics;
using Rowles.LeanCorpus.Store;

namespace Rowles.LeanCorpus.CompoundDurabilitySpike;

internal static partial class SpikeRunner
{
    private static readonly int[] CardinalityCounts = [1, 4, 16, 64, 128];
    private static readonly string[] CardinalityModes = ["equal_size_partitions", "production_shaped_partitions"];
    private static readonly byte[] MarkerBytes = Enumerable.Range(0, 4096).Select(static value => (byte)value).ToArray();

    public static async Task<int> RunCardinalityLaunchAsync(string root, Arguments arguments)
    {
        var paths = new SpikePaths(root);
        int launch = arguments.Int32("launch");
        if (launch is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(arguments), "Spike 1B cardinality launches are 1 through 3.");
        string platform = arguments.Required("platform");
        string runId = File.ReadAllText(Path.Combine(root, "run-id.txt")).Trim();
        string experimentSha = File.ReadAllText(Path.Combine(root, "experiment-sha.txt")).Trim();
        string baseSha = File.ReadAllText(Path.Combine(root, "base-sha.txt")).Trim();
        List<CardinalityCell> cells = ReadCardinalityOrder(root, launch);
        if (cells.Count != 20)
            throw new InvalidDataException($"Launch {launch} has {cells.Count} resolved cardinality cells; expected 20.");

        foreach (CardinalityCell cell in cells)
        for (int observation = 0; observation <= 5; observation++)
        {
            bool warmup = observation == 0;
            string trialId = $"card-l{launch}-c{cell.CellOrder}-o{observation}-p{cell.PayloadBytes}-k{cell.ObjectCount}-{cell.PartitionMode}";
            if (Csv.Read(paths.CardinalityTrialsPath).Any(row =>
                row["launch"] == launch.ToString(CultureInfo.InvariantCulture)
                && row["cell_order"] == cell.CellOrder.ToString(CultureInfo.InvariantCulture)
                && row["observation"] == observation.ToString(CultureInfo.InvariantCulture)))
                throw new IOException($"Cardinality observation already exists for launch {launch}, cell {cell.CellOrder}, observation {observation}; failed observations are never replaced.");

            string trialPath = paths.TrialPath(trialId);
            if (Directory.Exists(trialPath))
                throw new IOException($"Trial path already exists: {trialId}");
            Directory.CreateDirectory(trialPath);
            var workerArguments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mode"] = "cardinality-worker",
                ["root"] = root,
                ["platform"] = platform,
                ["launch"] = launch.ToString(CultureInfo.InvariantCulture),
                ["cell-order"] = cell.CellOrder.ToString(CultureInfo.InvariantCulture),
                ["observation"] = observation.ToString(CultureInfo.InvariantCulture),
                ["warmup"] = warmup.ToString().ToLowerInvariant(),
                ["trial-id"] = trialId,
                ["payload-bytes"] = cell.PayloadBytes.ToString(CultureInfo.InvariantCulture),
                ["object-count"] = cell.ObjectCount.ToString(CultureInfo.InvariantCulture),
                ["partition-mode"] = cell.PartitionMode,
                ["partition-vector-sha256"] = cell.PartitionVectorSha256,
                ["run-id"] = runId,
                ["experiment-sha"] = experimentSha,
                ["base-sha"] = baseSha
            };
            string logPath = Path.Combine(paths.LogsDirectory, trialId + ".log");
            ProcessResult result = await SpikeInfrastructure.RunWorkerAsync(root, logPath, workerArguments);
            EnsureCardinalityFailureRecorded(paths, workerArguments, result, logPath);
            if (result.ExitCode != 0)
                Console.Error.WriteLine($"cardinality trial {trialId} exited {result.ExitCode}: {result.StandardError.Trim()}");
        }

        foreach (var group in Csv.Read(paths.CardinalityTrialsPath)
                     .Where(row => row["launch"] == launch.ToString(CultureInfo.InvariantCulture)
                         && row["warmup"] == "false" && !string.IsNullOrEmpty(row["error"]))
                     .GroupBy(row => (PayloadBytes: row["payload_bytes"], ObjectCount: row["object_count"], Mode: row["partition_mode"])))
        {
            if (group.Count() > 1)
                throw new InvalidDataException($"Launch {launch} cardinality cell {group.Key} has {group.Count()} failed measured observations; stop this platform for review.");
        }
        return 0;
    }

    public static Task<int> RunCardinalityWorkerAsync(string root, Arguments arguments)
    {
        var paths = new SpikePaths(root);
        string trialId = arguments.Required("trial-id");
        int payloadBytes = arguments.Int32("payload-bytes");
        int objectCount = arguments.Int32("object-count");
        string partitionMode = arguments.Required("partition-mode");
        string trialPath = paths.TrialPath(trialId);
        int launch = arguments.Int32("launch");
        int cellOrder = arguments.Int32("cell-order");
        int observation = arguments.Int32("observation");
        bool warmup = arguments.Boolean("warmup");
        var row = new CsvRowBuilder(CsvSchemas.Cardinality);
        string? error = null;

        try
        {
            if (!Dataset.CardinalityPayloadSizes.Contains(payloadBytes) || !CardinalityCounts.Contains(objectCount)
                || !CardinalityModes.Contains(partitionMode, StringComparer.Ordinal))
                throw new ArgumentOutOfRangeException(nameof(arguments), "Cardinality cell is outside the locked Spike 1B matrix.");

            Dictionary<string, string> partitionRow = Csv.Read(paths.CardinalityPartitionsPath)
                .Single(candidate => candidate["payload_bytes"] == payloadBytes.ToString(CultureInfo.InvariantCulture)
                    && candidate["object_count"] == objectCount.ToString(CultureInfo.InvariantCulture)
                    && candidate["partition_mode"] == partitionMode);
            long[] partitionVector = JsonSerializer.Deserialize<long[]>(partitionRow["partition_vector_bytes"])
                ?? throw new InvalidDataException("The cardinality partition vector is invalid JSON.");
            string vectorSha = HashUtf8(JsonSerializer.Serialize(partitionVector));
            if (partitionVector.Length != objectCount || partitionVector.Any(static bytes => bytes <= 0)
                || partitionVector.Sum() != payloadBytes
                || vectorSha != arguments.Required("partition-vector-sha256"))
                throw new InvalidDataException("The cardinality partition vector failed its count, payload-total, or pre-run SHA validation.");

            string payloadPath = paths.CardinalityPayloadPath(payloadBytes);
            string expectedSha = File.ReadAllText(paths.CardinalityPayloadShaPath(payloadBytes)).Trim();
            if (new FileInfo(payloadPath).Length != payloadBytes || Dataset.HashFile(payloadPath) != expectedSha)
                throw new InvalidDataException($"The canonical {payloadBytes}-byte payload failed its length or SHA-256 check.");

            string[] filePaths = PartitionPayload(payloadPath, partitionVector, trialPath);
            using var detailedMeasurement = FileSystemDiagnostics.BeginDetailedMeasurement();
            var observer = new SpikeObserver();
            observer.Attach();
            FileSystemDiagnosticsSnapshot before = FileSystemDiagnostics.GetSnapshot();
            long started = Stopwatch.GetTimestamp();
            foreach (string filePath in filePaths)
                DirectoryFsync.SyncFile(filePath, strict: true);
            DirectoryFsync.Sync(trialPath, strict: true);
            IndexAtomicFileWriter.Write(
                Path.Combine(trialPath, "publication.marker"),
                durable: true,
                syncDirectory: true,
                stream => stream.Write(MarkerBytes));
            long completed = Stopwatch.GetTimestamp();
            FileSystemDiagnosticsSnapshot after = FileSystemDiagnostics.GetSnapshot();
            SpikeInstrumentation.Observer = null;

            bool reconstructionPass = VerifyPayload(filePaths, payloadBytes, expectedSha);
            row.Set("run_id", arguments.Required("run-id"));
            row.Set("platform_id", arguments.Required("platform"));
            row.Set("experiment_sha", arguments.Required("experiment-sha"));
            row.Set("base_sha", arguments.Required("base-sha"));
            row.Set("launch", launch);
            row.Set("cell_order", cellOrder);
            row.Set("observation", observation);
            row.Set("warmup", warmup);
            row.Set("object_count", objectCount);
            row.Set("payload_bytes", payloadBytes);
            row.Set("payload_sha256", expectedSha);
            row.Set("partition_mode", partitionMode);
            row.Set("partition_vector_bytes", JsonSerializer.Serialize(partitionVector));
            row.Set("partition_vector_sha256", vectorSha);
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
                error = "Partitioned files did not reproduce the canonical payload byte-for-byte and SHA-256.";

            long filePersistRequests = after.FileSyncCount - before.FileSyncCount;
            long expectedFilePersistRequests = objectCount + 1L;
            if (filePersistRequests != observer.FilePersistRequests)
                error = AppendError(error, "FileSystemDiagnostics FileSyncCount did not match observed file persistence requests.");
            if (filePersistRequests != expectedFilePersistRequests)
                error = AppendError(error, $"Expected {expectedFilePersistRequests} explicit file persistence requests; observed {filePersistRequests}.");
            if (observer.DirectoryPersistRequests != 2)
                error = AppendError(error, $"Expected two directory persistence requests; observed {observer.DirectoryPersistRequests}.");
            if (observer.AtomicReplaceCount != 1)
                error = AppendError(error, $"Expected one atomic publication-marker replacement; observed {observer.AtomicReplaceCount}.");

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
            SpikeInstrumentation.Observer = null;
            error = exception.ToString();
            PopulateFailedCardinalityRow(row, arguments, payloadBytes, objectCount, partitionMode, trialId, paths, error);
            row.Append(paths.CardinalityTrialsPath);
            PreserveFailedTrial(paths, trialPath, trialId);
            Console.Error.WriteLine(error);
            return Task.FromResult(1);
        }
    }

    private static string[] PartitionPayload(string payloadPath, IReadOnlyList<long> partitions, string directory)
    {
        string[] paths = new string[partitions.Count];
        byte[] buffer = GC.AllocateUninitializedArray<byte>(1024 * 1024);
        using var input = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            buffer.Length, FileOptions.SequentialScan);
        for (int index = 0; index < partitions.Count; index++)
        {
            string path = Path.Combine(directory, $"payload-{index:D3}.bin");
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                buffer.Length, FileOptions.SequentialScan);
            long remaining = partitions[index];
            while (remaining > 0)
            {
                int count = (int)Math.Min(buffer.Length, remaining);
                input.ReadExactly(buffer.AsSpan(0, count));
                output.Write(buffer, 0, count);
                remaining -= count;
            }
            paths[index] = path;
        }
        if (input.ReadByte() != -1)
            throw new InvalidDataException("The partition vector did not consume the complete canonical payload.");
        return paths;
    }

    private static bool VerifyPayload(IReadOnlyList<string> filePaths, long expectedLength, string expectedSha)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(1024 * 1024);
        long total = 0;
        foreach (string path in filePaths)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                buffer.Length, FileOptions.SequentialScan);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, read));
                total += read;
            }
        }
        string actualSha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return total == expectedLength && string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase);
    }

    private static List<CardinalityCell> ReadCardinalityOrder(string root, int launch)
        => Csv.Read(Path.Combine(root, "cardinality-order.csv"))
            .Where(row => int.Parse(row["launch"], CultureInfo.InvariantCulture) == launch)
            .OrderBy(row => int.Parse(row["cell_order"], CultureInfo.InvariantCulture))
            .Select(row => new CardinalityCell(
                int.Parse(row["cell_order"], CultureInfo.InvariantCulture),
                int.Parse(row["payload_bytes"], CultureInfo.InvariantCulture),
                int.Parse(row["object_count"], CultureInfo.InvariantCulture),
                row["partition_mode"],
                row["partition_vector_sha256"]))
            .ToList();

    private static void EnsureCardinalityFailureRecorded(
        SpikePaths paths,
        IReadOnlyDictionary<string, string> workerArguments,
        ProcessResult result,
        string logPath)
    {
        int launch = int.Parse(workerArguments["launch"], CultureInfo.InvariantCulture);
        int cellOrder = int.Parse(workerArguments["cell-order"], CultureInfo.InvariantCulture);
        int observation = int.Parse(workerArguments["observation"], CultureInfo.InvariantCulture);
        if (Csv.Read(paths.CardinalityTrialsPath).Any(row =>
            row["launch"] == launch.ToString(CultureInfo.InvariantCulture)
            && row["cell_order"] == cellOrder.ToString(CultureInfo.InvariantCulture)
            && row["observation"] == observation.ToString(CultureInfo.InvariantCulture)))
            return;
        var row = new CsvRowBuilder(CsvSchemas.Cardinality);
        row.Set("run_id", workerArguments["run-id"]);
        row.Set("platform_id", workerArguments["platform"]);
        row.Set("experiment_sha", workerArguments["experiment-sha"]);
        row.Set("base_sha", workerArguments["base-sha"]);
        row.Set("launch", launch);
        row.Set("cell_order", cellOrder);
        row.Set("observation", observation);
        row.Set("warmup", workerArguments["warmup"]);
        row.Set("object_count", workerArguments["object-count"]);
        row.Set("payload_bytes", workerArguments["payload-bytes"]);
        row.Set("partition_mode", workerArguments["partition-mode"]);
        row.Set("partition_vector_sha256", workerArguments["partition-vector-sha256"]);
        row.Set("payload_reconstruction_pass", false);
        row.Set("error", $"Worker exited {result.ExitCode}; log={Path.GetFileName(logPath)}; stderr={result.StandardError.Trim()}");
        row.Append(paths.CardinalityTrialsPath);
    }

    private static void PopulateFailedCardinalityRow(
        CsvRowBuilder row,
        Arguments arguments,
        int payloadBytes,
        int objectCount,
        string partitionMode,
        string trialId,
        SpikePaths paths,
        string error)
    {
        string shaPath = paths.CardinalityPayloadShaPath(payloadBytes);
        string expectedSha = File.Exists(shaPath) ? File.ReadAllText(shaPath).Trim() : string.Empty;
        row.Set("run_id", arguments.Required("run-id"));
        row.Set("platform_id", arguments.Required("platform"));
        row.Set("experiment_sha", arguments.Required("experiment-sha"));
        row.Set("base_sha", arguments.Required("base-sha"));
        row.Set("launch", arguments.Int32("launch"));
        row.Set("cell_order", arguments.Int32("cell-order"));
        row.Set("observation", arguments.Int32("observation"));
        row.Set("warmup", arguments.Boolean("warmup"));
        row.Set("object_count", objectCount);
        row.Set("payload_bytes", payloadBytes);
        row.Set("payload_sha256", expectedSha);
        row.Set("partition_mode", partitionMode);
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

    private static string HashUtf8(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record CardinalityCell(
        int CellOrder,
        int PayloadBytes,
        int ObjectCount,
        string PartitionMode,
        string PartitionVectorSha256);
}
