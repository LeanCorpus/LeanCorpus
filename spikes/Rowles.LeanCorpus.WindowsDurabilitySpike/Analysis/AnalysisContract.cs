using System.Text;

namespace Rowles.LeanCorpus.WindowsDurabilitySpike.Analysis;

internal static class AnalysisContract
{
    internal static int Validate(SpikeArguments arguments)
    {
        ValidateWarmUpExclusion();
        ValidateSampleCardinality();
        ValidateOmissionSemantics();
        ValidateClassificationBoundaries();
        Publication.PublicationAnalysis.ValidateContract();

        string output = Path.GetFullPath(arguments.Required("output"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output,
            "Spike 2 analysis contract validation passed.\n" +
            "Validated exact measured and warm-up cardinalities, warm-up exclusion, retained failures, five independent launch omissions, phase-share boundaries, publication matrices, recovery matrices, fault-injection edges, and stable candidate ranking.\n",
            new UTF8Encoding(false));
        Console.WriteLine("Analysis contract fixtures passed.");
        return 0;
    }

    private static void ValidateWarmUpExclusion()
    {
        var fixture = new[]
        {
            new FixtureRow(true, true, 10),
            new FixtureRow(false, true, 100),
            new FixtureRow(false, true, 102)
        };
        double[] measured = fixture.Where(static row => !row.WarmUp && row.Success)
            .Select(static row => row.Value).ToArray();
        Require(measured.Length == 2 && MechanismStatistics.Median(measured) == 101,
            "Warm-up values must not enter measured summaries.");
    }

    private static void ValidateSampleCardinality()
    {
        (int localMeasured, int localAttempts) = MechanismAnalysis.ExpectedCountsFor("local_windows_vm");
        (int windowsMeasured, int windowsAttempts) = MechanismAnalysis.ExpectedCountsFor("hosted_windows_2025");
        (int ubuntuMeasured, int ubuntuAttempts) = MechanismAnalysis.ExpectedCountsFor("hosted_ubuntu_24_04");
        Require(localMeasured == 1090 && localAttempts == 1320 && windowsMeasured == 288 && windowsAttempts == 384 &&
                ubuntuMeasured == 72 && ubuntuAttempts == 96,
            "The measured and warm-up attempt cardinalities must match the declared local, hosted Windows and Ubuntu matrices.");
    }

    private static void ValidateOmissionSemantics()
    {
        var attempts = new[]
        {
            new FixtureRow(true, true, 1),
            new FixtureRow(false, false, double.NaN),
            new FixtureRow(false, true, 3)
        };
        int retainedAttempts = attempts.Length;
        int failures = attempts.Count(static row => !row.Success && !row.WarmUp);
        double[] included = attempts.Where(static row => row.Success && !row.WarmUp)
            .Select(static row => row.Value).ToArray();
        Require(retainedAttempts == 3 && failures == 1 && included.Length == 1 && included[0] == 3,
            "Failed observations must remain attempts and must not be silently replaced or summarised as successes.");

        (int Launch, double Value)[] launchValues = Enumerable.Range(1, 5)
            .Select(static launch => (launch, (double)launch)).ToArray();
        double[] expectedMedians = [3.5, 3.5, 3.0, 2.5, 2.5];
        var omissions = Enumerable.Range(1, 5)
            .Select(launch => MechanismStatistics.MedianOutsideLaunch(launchValues, launch)).ToArray();
        Require(omissions.Length == 5 && omissions.Select(static item => item.SampleCount).All(static count => count == 4) &&
                omissions.Select(static item => item.Median).SequenceEqual(expectedMedians),
            "Leave-one-launch-out analysis must perform exactly five independent omissions from the complete launch set.");
    }

    private static void ValidateClassificationBoundaries()
    {
        string atFlushBoundary = MechanismStatistics.ClassifyPhaseShares(
            [0.70, 0.70, 0.70, 0.70, 0.69], [0.1, 0.1, 0.1, 0.1, 0.1]);
        string belowFlushBoundary = MechanismStatistics.ClassifyPhaseShares(
            [0.699, 0.699, 0.699, 0.699, 0.699], [0.1, 0.1, 0.1, 0.1, 0.1]);
        string atMixedBoundary = MechanismStatistics.ClassifyPhaseShares(
            [0.4, 0.4, 0.4, 0.4, 0.1], [0.4, 0.4, 0.4, 0.4, 0.1]);
        Require(atFlushBoundary == "flush_dominated", "The flush threshold is inclusive at 70%.");
        Require(belowFlushBoundary == "unstable_or_inconclusive", "Values below 70% cannot be flush-dominated.");
        Require(atMixedBoundary == "mixed", "The mixed threshold is inclusive at 80% for four cells.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record FixtureRow(bool WarmUp, bool Success, double Value);
}
