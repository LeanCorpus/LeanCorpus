using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rowles.DataForge;
using Rowles.LeanCorpus.Index.Indexer;

namespace Rowles.LeanCorpus.CompoundDurabilitySpike;

internal static partial class SpikeRunner
{
    private static readonly int[] ProductionLaunches = [1, 2, 3, 4, 5];
    private static readonly int[] ProductionOrderSeeds = [20261201, 20261202, 20261203, 20261204, 20261205];

    public static async Task<int> PrepareAsync(string root, Arguments arguments)
    {
        var paths = new SpikePaths(root);
        if (Directory.EnumerateFileSystemEntries(paths.Root).Any())
            throw new IOException($"Evidence root already contains data: {paths.Root}");
        paths.EnsureDirectories();

        string platform = arguments.Required("platform");
        string repository = Path.GetFullPath(arguments.Optional("repo", SpikeInfrastructure.FindRepositoryRoot()));
        (string experimentSha, string baseSha) = EvidenceMetadata.Write(paths.Root, platform, repository, arguments);
        string runId = SpikeInfrastructure.NewRunId();
        WriteText(Path.Combine(paths.Root, "experiment-sha.txt"), experimentSha);
        WriteText(Path.Combine(paths.Root, "base-sha.txt"), baseSha);
        WriteText(Path.Combine(paths.Root, "run-id.txt"), runId);
        WriteText(Path.Combine(paths.Root, "started-utc.txt"), SpikeInfrastructure.CurrentUtc());

        DataForgeDatasetIdentity identity = Dataset.Generate(paths);
        Dataset.CreateCardinalityPayloads(paths);
        WriteProductionOrder(paths.Root);
        Csv.Create(paths.ProductionTrialsPath, CsvSchemas.Production);
        Csv.Create(paths.ProductionPairsPath, CsvSchemas.ProductionPairs);

        foreach (string representation in new[] { "loose", "compound" })
        {
            string baselinePath = paths.BaselinePath(representation);
            if (Directory.Exists(baselinePath))
                throw new IOException($"Baseline path already exists: {representation}");
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            var workerArguments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mode"] = "baseline-worker",
                ["root"] = paths.Root,
                ["representation"] = representation,
                ["index-path"] = baselinePath
            };
            Console.WriteLine($"Building {representation} baseline as nine committed 10,000-document segments.");
            ProcessResult result = await SpikeInfrastructure.RunWorkerAsync(
                paths.Root,
                Path.Combine(paths.LogsDirectory, "baseline-" + representation + ".log"),
                workerArguments);
            if (result.ExitCode != 0)
                throw new InvalidDataException($"Baseline worker '{representation}' failed: {result.StandardError}");

            IndexTopology topology = Topology.Read(baselinePath, representation, inspectEveryDocument: true);
            if (!Topology.HasExpectedBaseline(topology))
                throw new InvalidDataException($"The {representation} baseline does not contain the required nine exact 10,000-document segments.");
            Topology.WriteManifest(paths.BaselineTopologyPath(representation), representation, topology);
        }

        IndexTopology looseTopology = Topology.Read(paths.BaselinePath("loose"), "loose", inspectEveryDocument: false);
        IndexTopology compoundTopology = Topology.Read(paths.BaselinePath("compound"), "compound", inspectEveryDocument: false);
        if (!SameLogicalTopology(looseTopology, compoundTopology))
            throw new InvalidDataException("Loose and compound baseline logical segment boundaries differ; platform measurement is stopped.");

        var metadata = new
        {
            runId,
            platformId = platform,
            experimentSha,
            baseSha,
            startedUtc = File.ReadAllText(Path.Combine(paths.Root, "started-utc.txt")).Trim(),
            repository,
            evidenceDirectory = paths.Root,
            canonicalContentSha256 = identity.ContentSha256,
            datasetId = identity.GetShortKey(),
            productionLaunches = ProductionLaunches,
            productionOrderSeeds = ProductionOrderSeeds,
            baselineSegmentCount = 9,
            baselineDocumentsPerSegment = 10_000
        };
        WriteJson(Path.Combine(paths.Root, "run-metadata.json"), metadata);
        Console.WriteLine($"experiment_sha={experimentSha}");
        Console.WriteLine($"DataForgeVersion={identity.DataForgeVersion} profile={identity.ProfileId} v{identity.ProfileVersion} seed={identity.Seed} records={identity.RecordCount}");
        Console.WriteLine($"canonical_content_sha256={identity.ContentSha256}");
        Console.WriteLine($"Prepared platform={platform} root={paths.Root}");
        return 0;
    }

    public static Task<int> PrepareCardinalityAsync(string root, Arguments arguments)
    {
        var paths = new SpikePaths(root);
        if (!File.Exists(paths.ProductionTrialsPath))
            throw new FileNotFoundException("Production observations are required before deriving production-shaped cardinality partitions.");
        if (File.Exists(paths.CardinalityOrderPath) || File.Exists(paths.CardinalityPartitionsPath)
            || File.Exists(paths.CardinalityTrialsPath))
            throw new IOException("Cardinality evidence already exists. Refusing to replace observations or partition vectors.");

        long[] sourceSizes = ReadProductionPartitionSource(paths.ProductionTrialsPath);
        var partitionRows = new List<(int PayloadBytes, int ObjectCount, string Mode, string Source, long[] SourceSizes, long[] Vector)>();
        foreach (int payloadBytes in Dataset.CardinalityPayloadSizes)
        foreach (int objectCount in CardinalityCounts)
        {
            partitionRows.Add((payloadBytes, objectCount, "equal_size_partitions", "equal integer partitions", [], EqualPartitions(payloadBytes, objectCount)));
            partitionRows.Add((payloadBytes, objectCount, "production_shaped_partitions",
                "median-equivalent observed loose member proportions from compound-durable-fresh measured batch",
                sourceSizes, ProductionShapedPartitions(payloadBytes, objectCount, sourceSizes)));
        }

        Csv.Create(paths.CardinalityPartitionsPath, CsvSchemas.CardinalityPartitions);
        foreach (var item in partitionRows)
        {
            string vectorJson = JsonSerializer.Serialize(item.Vector);
            Csv.Append(paths.CardinalityPartitionsPath, CsvSchemas.CardinalityPartitions,
            [
                item.PayloadBytes,
                item.ObjectCount,
                item.Mode,
                item.Source,
                item.SourceSizes.Length,
                JsonSerializer.Serialize(item.SourceSizes),
                vectorJson,
                HashUtf8(vectorJson),
                item.Vector.Sum()
            ]);
        }

        WriteCardinalityOrder(paths.Root, partitionRows);
        Csv.Create(paths.CardinalityTrialsPath, CsvSchemas.Cardinality);
        Console.WriteLine($"Cardinality partition plan written with source_member_count={sourceSizes.Length}.");
        return Task.FromResult(0);
    }

    public static Task<int> VerifyDatasetsAsync(string aggregateRoot, Arguments arguments)
    {
        string[][] platforms =
        [
            ["linux-ext4", "Linux/ext4"],
            ["windows-ntfs", "Windows/NTFS"]
        ];
        var identities = new List<(string Platform, string ContentSha, JsonDocument Document)>();
        foreach (string[] platform in platforms)
        {
            string path = Path.Combine(aggregateRoot, platform[0], "dataset-identity.json");
            if (!File.Exists(path))
                throw new FileNotFoundException($"The {platform[1]} DataForge identity is required before any measured run.", path);
            JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement identity = document.RootElement;
            if (identity.GetProperty("dataForgeVersion").GetInt32() <= 0
                || identity.GetProperty("profileId").GetString() != "leancorpus-search"
                || identity.GetProperty("profileVersion").GetInt32() != 1
                || identity.GetProperty("generation_parameters").GetProperty("seed").GetUInt64() != 42
                || identity.GetProperty("generation_parameters").GetProperty("record_count").GetInt32() != 100_000)
            {
                document.Dispose();
                throw new InvalidDataException($"The {platform[1]} DataForge identity does not match the locked seed, profile, version, and record count.");
            }

            string platformRoot = Path.Combine(aggregateRoot, platform[0]);
            string trialsPath = Path.Combine(platformRoot, "production-trials.csv");
            if (File.Exists(trialsPath) && Csv.Read(trialsPath).Count != 0)
            {
                document.Dispose();
                throw new InvalidOperationException("Cross-platform dataset identity must be verified before measured trials exist.");
            }

            identities.Add((platform[0], identity.GetProperty("canonical_content_sha256").GetString() ?? string.Empty, document));
        }

        try
        {
            if (identities.Any(static item => item.ContentSha.Length != 64)
                || !string.Equals(identities[0].ContentSha, identities[1].ContentSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Linux and Windows DataForge canonical content SHA-256 values differ; no measured run may start.");

            string experimentSha = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(aggregateRoot)));
            var crossPlatformIdentity = new
            {
                experiment_sha = experimentSha,
                canonical_content_sha256 = identities[0].ContentSha,
                platforms = identities.Select(item => new
                {
                    platform_id = item.Platform,
                    canonical_content_sha256 = item.ContentSha,
                    dataset_identity = item.Document.RootElement.Clone()
                })
            };
            WriteJson(Path.Combine(aggregateRoot, "dataset-identity-cross-platform.json"), crossPlatformIdentity);
            Console.WriteLine($"cross_platform_canonical_content_sha256={identities[0].ContentSha}");
            return Task.FromResult(0);
        }
        finally
        {
            foreach (var identity in identities)
                identity.Document.Dispose();
        }
    }

    private static long[] ReadProductionPartitionSource(string productionTrialsPath)
    {
        var rows = Csv.Read(productionTrialsPath)
            .Where(static row => row["lifecycle"] == "fresh" && row["representation"] == "compound"
                && row["durable"] == "true" && row["warmup"] == "false"
                && string.IsNullOrEmpty(row["error"]))
            .OrderBy(static row => int.Parse(row["launch"], CultureInfo.InvariantCulture))
            .ThenBy(static row => int.Parse(row["observation"], CultureInfo.InvariantCulture))
            .ToArray();
        if (rows.Length == 0)
            throw new InvalidDataException("No successful measured compound-durable-fresh observation is available for the production-shaped partition source.");

        long[] selected = JsonSerializer.Deserialize<long[]>(rows[0]["pack_member_size_vector_bytes"])
            ?? throw new InvalidDataException("The selected production observation has no pack-member size vector.");
        if (selected.Length == 0 || selected.Any(static size => size <= 0))
            throw new InvalidDataException("The selected production pack-member size vector is empty or invalid.");
        if (rows.Any(row =>
            !JsonSerializer.Deserialize<long[]>(row["pack_member_size_vector_bytes"])!.SequenceEqual(selected)))
            throw new InvalidDataException("Successful compound-durable-fresh observations produced different member-size vectors; cardinality shaping is not reproducible.");
        return selected;
    }

    private static long[] EqualPartitions(long totalBytes, int count)
    {
        var result = new long[count];
        long quotient = totalBytes / count;
        int remainder = (int)(totalBytes % count);
        for (int index = 0; index < count; index++)
            result[index] = quotient + (index < remainder ? 1 : 0);
        return result;
    }

    private static long[] ProductionShapedPartitions(long payloadBytes, int objectCount, IReadOnlyList<long> sourceSizes)
    {
        var weights = new long[objectCount];
        if (objectCount <= sourceSizes.Count)
        {
            for (int sourceIndex = 0; sourceIndex < sourceSizes.Count; sourceIndex++)
                weights[(int)((long)sourceIndex * objectCount / sourceSizes.Count)] += sourceSizes[sourceIndex];
        }
        else
        {
            long assignedSourceBytes = sourceSizes.Sum();
            for (int sourceIndex = 0; sourceIndex < sourceSizes.Count; sourceIndex++)
            {
                int firstSlot = (int)((long)sourceIndex * objectCount / sourceSizes.Count);
                int nextSlot = (int)((long)(sourceIndex + 1) * objectCount / sourceSizes.Count);
                int slotCount = nextSlot - firstSlot;
                long quotient = sourceSizes[sourceIndex] / slotCount;
                long remainder = sourceSizes[sourceIndex] % slotCount;
                for (int slot = 0; slot < slotCount; slot++)
                    weights[firstSlot + slot] = quotient + (slot < remainder ? 1 : 0);
            }
            if (weights.Sum() != assignedSourceBytes)
                throw new InvalidDataException("Production-shaped weight refinement did not preserve source bytes.");
        }

        long weightTotal = weights.Sum();
        var scaled = new long[objectCount];
        long scaledTotal = 0;
        var fractions = new (int Index, decimal Fraction)[objectCount];
        for (int index = 0; index < objectCount; index++)
        {
            decimal exact = (decimal)weights[index] * payloadBytes / weightTotal;
            scaled[index] = (long)decimal.Floor(exact);
            scaledTotal += scaled[index];
            fractions[index] = (index, exact - scaled[index]);
        }
        long remaining = payloadBytes - scaledTotal;
        foreach (var fraction in fractions.OrderByDescending(static item => item.Fraction).ThenBy(static item => item.Index).Take((int)remaining))
            scaled[fraction.Index]++;
        return scaled;
    }

    private static void WriteProductionOrder(string root)
    {
        string[] cells = (from representation in new[] { "loose", "compound" }
                          from durability in new[] { "disabled", "enabled" }
                          from lifecycle in new[] { "fresh", "reopened-first", "reopened-steady" }
                          select $"{representation}-{durability}-{lifecycle}")
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        string path = Path.Combine(root, "execution-order.csv");
        string[] columns = ["launch", "shuffle_seed", "cell_order", "cell_id"];
        Csv.Create(path, columns);
        for (int launch = 1; launch <= ProductionLaunches.Length; launch++)
        {
            string[] shuffled = cells.ToArray();
            new Random(ProductionOrderSeeds[launch - 1]).Shuffle(shuffled.AsSpan());
            for (int index = 0; index < shuffled.Length; index++)
                Csv.Append(path, columns, [launch, ProductionOrderSeeds[launch - 1], index + 1, shuffled[index]]);
        }
    }

    private static void WriteCardinalityOrder(
        string root,
        IReadOnlyList<(int PayloadBytes, int ObjectCount, string Mode, string Source, long[] SourceSizes, long[] Vector)> partitions)
    {
        string[] columns = ["launch", "shuffle_seed", "cell_order", "payload_bytes", "object_count", "partition_mode", "partition_vector_sha256"];
        Csv.Create(Path.Combine(root, "cardinality-order.csv"), columns);
        var cells = partitions.Select(item => new CardinalityPlanCell(item.PayloadBytes, item.ObjectCount, item.Mode,
                HashUtf8(JsonSerializer.Serialize(item.Vector))))
            .OrderBy(static cell => cell.PayloadBytes)
            .ThenBy(static cell => cell.ObjectCount)
            .ThenBy(static cell => cell.PartitionMode, StringComparer.Ordinal)
            .ToArray();
        int[] seeds = [20261211, 20261212, 20261213];
        for (int launch = 1; launch <= seeds.Length; launch++)
        {
            CardinalityPlanCell[] shuffled = cells.ToArray();
            new Random(seeds[launch - 1]).Shuffle(shuffled.AsSpan());
            for (int index = 0; index < shuffled.Length; index++)
            {
                CardinalityPlanCell cell = shuffled[index];
                Csv.Append(Path.Combine(root, "cardinality-order.csv"), columns,
                    [launch, seeds[launch - 1], index + 1, cell.PayloadBytes, cell.ObjectCount, cell.PartitionMode, cell.PartitionVectorSha256]);
            }
        }
    }

    private static bool SameLogicalTopology(IndexTopology left, IndexTopology right)
        => left.Segments.Count == right.Segments.Count
            && left.Segments.Zip(right.Segments).All(static pair =>
                pair.First.MinDocumentOrdinal == pair.Second.MinDocumentOrdinal
                && pair.First.MaxDocumentOrdinal == pair.Second.MaxDocumentOrdinal
                && pair.First.DocumentCount == pair.Second.DocumentCount);

    private static void WriteText(string path, string value)
        => File.WriteAllText(path, value + "\n", new UTF8Encoding(false));

    private static void WriteJson<T>(string path, T value)
        => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false));

    private sealed record CardinalityPlanCell(int PayloadBytes, int ObjectCount, string PartitionMode, string PartitionVectorSha256);
}
