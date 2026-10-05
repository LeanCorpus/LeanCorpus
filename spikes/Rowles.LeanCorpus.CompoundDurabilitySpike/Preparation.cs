using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Rowles.LeanCorpus.CompoundDurabilitySpike;

internal static partial class SpikeRunner
{
    public static async Task<int> PrepareAsync(string root, Arguments arguments)
    {
        var paths = new SpikePaths(root);
        paths.EnsureDirectories();
        string repository = arguments.Optional("repo", SpikeInfrastructure.FindRepositoryRoot());
        string baseSha = SpikeInfrastructure.BaseSha(arguments);
        string spikeSha = SpikeInfrastructure.SpikeSha(arguments);
        string runId = SpikeInfrastructure.NewRunId();
        File.WriteAllText(Path.Combine(root, "base-sha.txt"), baseSha + "\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "spike-sha.txt"), spikeSha + "\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "run-id.txt"), runId + "\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "started-utc.txt"), SpikeInfrastructure.CurrentUtc() + "\n", new UTF8Encoding(false));

        string datasetSource = arguments.Optional("dataset-source");
        var identity = string.IsNullOrWhiteSpace(datasetSource)
            ? Dataset.Generate(paths)
            : Dataset.ImportFrozen(paths, datasetSource);
        Dataset.CreateCardinalityPayload(paths);
        WriteProductionOrder(root);
        WriteCardinalityOrder(root);
        Csv.Create(paths.ProductionTrialsPath, CsvSchemas.Production);
        Csv.Create(paths.CardinalityTrialsPath, CsvSchemas.Cardinality);
        Csv.Create(paths.RecoveryTrialsPath, CsvSchemas.Recovery);

        string? platform = arguments.Optional("platform");
        if (string.IsNullOrWhiteSpace(platform))
            throw new ArgumentException("Pass --platform windows-ntfs or linux-ext4 when preparing one platform's evidence.");
        if (platform is not ("windows-ntfs" or "linux-ext4"))
            throw new ArgumentException($"Unknown primary platform '{platform}'.");

        Console.WriteLine($"dataset_id={identity.GetShortKey()}");
        Console.WriteLine($"DataForgeVersion={identity.DataForgeVersion} profile={identity.ProfileId} v{identity.ProfileVersion} seed={identity.Seed} records={identity.RecordCount}");
        Console.WriteLine($"canonical_content_sha256={identity.ContentSha256}");

        foreach (string representation in new[] { "loose", "compound" })
        {
            string baselinePath = paths.BaselinePath(representation);
            if (Directory.Exists(baselinePath))
                throw new IOException($"Baseline path already exists: {representation}");
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            var workerArguments = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mode"] = "baseline-worker",
                ["root"] = root,
                ["representation"] = representation,
                ["index-path"] = baselinePath
            };
            Console.WriteLine($"Building durable {representation} baseline (90,000 records).");
            ProcessResult result = await SpikeInfrastructure.RunWorkerAsync(
                root,
                Path.Combine(paths.LogsDirectory, "baseline-" + representation + ".log"),
                workerArguments);
            if (result.ExitCode != 0)
                throw new InvalidDataException($"Baseline worker '{representation}' failed: {result.StandardError}");
        }

        var runMetadata = new
        {
            runId,
            platformId = platform,
            baseSha,
            spikeSha,
            startedUtc = File.ReadAllText(Path.Combine(root, "started-utc.txt")).Trim(),
            repository,
            evidenceDirectory = root,
            corpusContentSha256 = identity.ContentSha256,
            datasetId = identity.GetShortKey()
        };
        File.WriteAllText(Path.Combine(root, "run-metadata.json"),
            JsonSerializer.Serialize(runMetadata, new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false));
        Console.WriteLine($"Prepared platform={platform} root={root}");
        return 0;
    }

    private static void WriteProductionOrder(string root)
    {
        string[] cells = (from representation in new[] { "loose", "compound" }
                          from durability in new[] { "disabled", "enabled" }
                          from lifecycle in new[] { "fresh", "reopened" }
                          select $"{representation}-{durability}-{lifecycle}")
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        string path = Path.Combine(root, "execution-order.csv");
        Csv.Create(path, ["launch", "cell_order", "cell_id"]);
        int[] seeds = [20261004, 20261005, 20261006];
        for (int launch = 1; launch <= seeds.Length; launch++)
        {
            string[] shuffled = cells.ToArray();
            new Random(seeds[launch - 1]).Shuffle(shuffled.AsSpan());
            for (int index = 0; index < shuffled.Length; index++)
                Csv.Append(path, ["launch", "cell_order", "cell_id"],
                    [launch, index + 1, shuffled[index]]);
        }
    }

    private static void WriteCardinalityOrder(string root)
    {
        int[] objectCounts = [1, 2, 4, 8, 16, 32, 64];
        string[] cells = (from objectCount in objectCounts
                          from durability in new[] { "disabled", "enabled" }
                          select $"{objectCount}-{durability}")
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        string path = Path.Combine(root, "cardinality-order.csv");
        Csv.Create(path, ["launch", "cell_order", "cell_id"]);
        int[] seeds = [20261104, 20261105, 20261106];
        for (int launch = 1; launch <= seeds.Length; launch++)
        {
            string[] shuffled = cells.ToArray();
            new Random(seeds[launch - 1]).Shuffle(shuffled.AsSpan());
            for (int index = 0; index < shuffled.Length; index++)
                Csv.Append(path, ["launch", "cell_order", "cell_id"],
                    [launch, index + 1, shuffled[index]]);
        }
    }
}
