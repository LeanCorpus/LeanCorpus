namespace Rowles.LeanCorpus.CompoundDurabilitySpike;

internal sealed class SpikePaths(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string DatasetDirectory => Path.Combine(Root, "dataset");
    public string RecordsPath => Path.Combine(DatasetDirectory, "records.ndjson");
    public string OffsetsPath => Path.Combine(DatasetDirectory, "record-offsets.bin");
    public string IdentityPath => Path.Combine(Root, "dataset-identity.json");
    public string PayloadPath => Path.Combine(Root, "cardinality-payload.bin");
    public string PayloadShaPath => Path.Combine(Root, "cardinality-payload.sha256");
    public string BaselinePath(string representation) => Path.Combine(Root, "baselines", representation);
    public string ProductionTrialsPath => Path.Combine(Root, "production-trials.csv");
    public string CardinalityTrialsPath => Path.Combine(Root, "cardinality-trials.csv");
    public string RecoveryTrialsPath => Path.Combine(Root, "recovery-trials.csv");
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
