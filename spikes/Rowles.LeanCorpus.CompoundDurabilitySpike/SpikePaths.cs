namespace Rowles.LeanCorpus.CompoundDurabilitySpike;

internal sealed class SpikePaths(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string DatasetDirectory => Path.Combine(Root, "dataset");
    public string RecordsPath => Path.Combine(DatasetDirectory, "records.ndjson");
    public string OffsetsPath => Path.Combine(DatasetDirectory, "record-offsets.bin");
    public string IdentityPath => Path.Combine(Root, "dataset-identity.json");
    public string CardinalityPayloadPath(int payloadBytes) => Path.Combine(Root, $"cardinality-payload-{payloadBytes}.bin");
    public string CardinalityPayloadShaPath(int payloadBytes) => Path.Combine(Root, $"cardinality-payload-{payloadBytes}.sha256");
    public string BaselinePath(string representation) => Path.Combine(Root, "baselines", representation);
    public string BaselineTopologyPath(string representation) => Path.Combine(Root, $"baseline-topology-{representation}.csv");
    public string ProductionTrialsPath => Path.Combine(Root, "production-trials.csv");
    public string ProductionPairsPath => Path.Combine(Root, "production-pairs.csv");
    public string CardinalityTrialsPath => Path.Combine(Root, "cardinality-trials.csv");
    public string CardinalityPartitionsPath => Path.Combine(Root, "cardinality-partitions.csv");
    public string RecoveryTrialsPath => Path.Combine(Root, "recovery-trials.csv");
    public string ExecutionOrderPath => Path.Combine(Root, "execution-order.csv");
    public string CardinalityOrderPath => Path.Combine(Root, "cardinality-order.csv");
    public string LogsDirectory => Path.Combine(Root, "logs");
    public string FailedDirectory => Path.Combine(Root, "failed-trials");
    public string TrialPath(string runId) => Path.Combine(Root, "trials", runId);

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DatasetDirectory);
        Directory.CreateDirectory(Path.Combine(Root, "baselines"));
        Directory.CreateDirectory(Path.Combine(Root, "trials"));
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(FailedDirectory);
    }
}
