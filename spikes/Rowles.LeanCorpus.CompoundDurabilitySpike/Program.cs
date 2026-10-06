using System.Globalization;

namespace Rowles.LeanCorpus.CompoundDurabilitySpike;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var parsed = Arguments.Parse(args);
            string mode = parsed.Required("mode");
            string root = Path.GetFullPath(parsed.Required("root"));
            Directory.CreateDirectory(root);

            return mode switch
            {
                "prepare" => await SpikeRunner.PrepareAsync(root, parsed),
                "baseline-worker" => await SpikeRunner.BuildBaselineAsync(root, parsed),
                "verify-datasets" => await SpikeRunner.VerifyDatasetsAsync(root, parsed),
                "production-launch" => await SpikeRunner.RunProductionLaunchAsync(root, parsed),
                "production-worker" => await SpikeRunner.RunProductionWorkerAsync(root, parsed),
                "prepare-cardinality" => await SpikeRunner.PrepareCardinalityAsync(root, parsed),
                "cardinality-launch" => await SpikeRunner.RunCardinalityLaunchAsync(root, parsed),
                "cardinality-worker" => await SpikeRunner.RunCardinalityWorkerAsync(root, parsed),
                "recovery-run" => await SpikeRunner.RunRecoveryAsync(root, parsed),
                "recovery-worker" => await SpikeRunner.RunRecoveryWorkerAsync(root, parsed),
                "analyse" => await SpikeRunner.AnalyseAsync(root, parsed),
                _ => throw new ArgumentException($"Unknown mode '{mode}'.")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}

internal sealed class Arguments
{
    private readonly Dictionary<string, string> _values;

    private Arguments(Dictionary<string, string> values) => _values = values;

    public static Arguments Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < args.Count; index++)
        {
            string key = args[index];
            if (!key.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Count)
                throw new ArgumentException($"Expected --name value, found '{key}'.");
            if (!values.TryAdd(key[2..], args[++index]))
                throw new ArgumentException($"Argument '{key}' was supplied more than once.");
        }
        return new Arguments(values);
    }

    public string Required(string name)
        => _values.TryGetValue(name, out var value)
            ? value
            : throw new ArgumentException($"Required argument --{name} is missing.");

    public string Optional(string name, string fallback = "")
        => _values.GetValueOrDefault(name, fallback);

    public int Int32(string name, int fallback = 0)
        => _values.TryGetValue(name, out var value)
            ? int.Parse(value, CultureInfo.InvariantCulture)
            : fallback;

    public bool Boolean(string name, bool fallback = false)
        => _values.TryGetValue(name, out var value)
            ? bool.Parse(value)
            : fallback;
}
