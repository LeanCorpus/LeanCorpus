using Rowles.LeanCorpus.WindowsDurabilitySpike.Analysis;
using Rowles.LeanCorpus.WindowsDurabilitySpike.Mechanisms;
using Rowles.LeanCorpus.WindowsDurabilitySpike.Publication;

namespace Rowles.LeanCorpus.WindowsDurabilitySpike;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                PrintHelp();
                return args.Length == 0 ? 2 : 0;
            }

            var options = SpikeArguments.Parse(args.Skip(1));
            return args[0] switch
            {
                "validate-analysis-contract" => AnalysisContract.Validate(options),
                "validate-observation-neutrality" => ObservationNeutrality.Validate(options),
                "capture-environment" => EvidencePreparation.CaptureEnvironment(options),
                "write-dataset-identity" => EvidencePreparation.WriteDatasetIdentity(options),
                "write-source-map" => EvidencePreparation.WriteSourceMap(options),
                "prepare-mechanism-order" => MechanismRunner.PrepareOrder(options),
                "run-mechanisms" => MechanismRunner.RunLaunch(options),
                "trace-mechanism-cell" => MechanismRunner.TraceCell(options),
                "analyse-mechanisms" => MechanismAnalysis.Run(options),
                "analyse-hosted-replication" => HostedReplicationAnalysis.Run(options),
                "prepare-publication-order" => PublicationRunner.PrepareOrder(options),
                "validate-publication-candidates" => PublicationRunner.ValidateCandidates(options),
                "write-publication-semantics" => PublicationRunner.WriteSemantics(options),
                "trace-publication-cell" => PublicationRunner.TraceCell(options),
                "run-publication" => PublicationRunner.RunLaunch(options),
                "analyse-publication" => PublicationAnalysis.Run(options),
                "recover" => RecoveryRunner.Run(options),
                "run-fault-injection" => RecoveryRunner.FaultInjection(options),
                "crash-child" => RecoveryRunner.Child(options),
                "recovery-inspect" => RecoveryRunner.Inspect(options),
                "validate-recovery-dataset" => RecoveryRunner.ValidateDataset(options),
                "verify-evidence" => EvidenceVerifier.Run(options),
                _ => UnknownCommand(args[0])
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Spike command failed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintHelp();
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("LeanCorpus Windows durability spike");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  validate-analysis-contract --output <path>");
        Console.WriteLine("  validate-observation-neutrality --output <path>");
        Console.WriteLine("  capture-environment --environment-class <local_windows_vm> --data-root <path> --order <csv> --evidence <new-directory>");
        Console.WriteLine("  write-dataset-identity --output <path> [--recovery-records <path>]");
        Console.WriteLine("  write-source-map --output <path>");
        Console.WriteLine("  prepare-mechanism-order --output <path>");
        Console.WriteLine("  run-mechanisms --data-root <path> --evidence <path> --order <path> --neutrality-validation <json> --launch <1..5>");
        Console.WriteLine("  trace-mechanism-cell --variant <A_current_full|D_leancorpus_wrapper> --payload-bytes <8388608|85983232> --file-count <16|64> --data-root <path> --evidence <path> --neutrality-validation <json>");
        Console.WriteLine("  analyse-mechanisms --input <launch-directory> --output <path>");
        Console.WriteLine("  analyse-hosted-replication --local-summary <json> --local-input <dir> --windows-summary <json> --windows-input <dir> --ubuntu-summary <json> --ubuntu-input <dir> --output <path>");
        Console.WriteLine("  prepare-publication-order --output <path>");
        Console.WriteLine("  validate-publication-candidates --data-root <path> --output <path>");
        Console.WriteLine("  write-publication-semantics --output <path>");
        Console.WriteLine("  trace-publication-cell --candidate <P0|P1|P2> --representation <loose|compound> --data-root <path> --evidence <path> --candidate-validation <json> --semantics <md> --neutrality-validation <json>");
        Console.WriteLine("  run-publication --data-root <path> --evidence <path> --order <path> --candidate-validation <path> --semantics <path> --neutrality-validation <json> --launch <1..5>");
        Console.WriteLine("  analyse-publication --input <evidence-root> --mechanism-summary <json> --hosted-summary <json> --candidate-validation <json> --semantics <md> --output <path>");
        Console.WriteLine("  recover --data-root <path> --evidence <path> --neutrality-validation <json> --dataset <path>");
        Console.WriteLine("  run-fault-injection --data-root <path> --evidence <path> --neutrality-validation <json> --dataset <path>");
        Console.WriteLine("  crash-child --mode <process-crash|hard-reset|fault> --candidate <P0|P1|P2> ...");
        Console.WriteLine("  recovery-inspect --trial-index <path> --output <path> --dataset <path> [--after-commit-return true]");
        Console.WriteLine("  validate-recovery-dataset --dataset <path>");
        Console.WriteLine("  verify-evidence --root <path>");
    }
}

internal sealed class SpikeArguments
{
    private readonly Dictionary<string, string> _values;

    private SpikeArguments(Dictionary<string, string> values) => _values = values;

    internal static SpikeArguments Parse(IEnumerable<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var iterator = args.GetEnumerator();
        while (iterator.MoveNext())
        {
            string key = iterator.Current;
            if (!key.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Expected an option, received '{key}'.");
            if (!iterator.MoveNext())
                throw new ArgumentException($"Missing value for '{key}'.");
            values.Add(key[2..], iterator.Current);
        }
        return new SpikeArguments(values);
    }

    internal string Required(string name)
        => _values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Required option '--{name}' is missing.");

    internal string Optional(string name, string fallback)
        => _values.TryGetValue(name, out string? value) ? value : fallback;

    internal int RequiredInt(string name)
        => int.TryParse(Required(name), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new ArgumentException($"Option '--{name}' must be an integer.");

    internal ulong RequiredUlong(string name)
        => ulong.TryParse(Required(name), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out ulong value)
            ? value
            : throw new ArgumentException($"Option '--{name}' must be an unsigned integer.");
}
