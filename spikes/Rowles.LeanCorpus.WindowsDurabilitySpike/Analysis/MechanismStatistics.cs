namespace Rowles.LeanCorpus.WindowsDurabilitySpike.Analysis;

internal static class MechanismStatistics
{
    internal static double Median(IReadOnlyList<double> values)
        => Percentile(values, 0.5);

    internal static (double Median, int SampleCount) MedianOutsideLaunch(
        IReadOnlyList<(int Launch, double Value)> values,
        int omittedLaunch)
    {
        double[] retained = values.Where(value => value.Launch != omittedLaunch)
            .Select(static value => value.Value).ToArray();
        return (Median(retained), retained.Length);
    }

    internal static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
            return double.NaN;
        double[] sorted = values.Order().ToArray();
        double position = percentile * (sorted.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        double fraction = position - lower;
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * fraction);
    }

    internal static (double Lower, double Upper) BootstrapMedian95(
        IReadOnlyList<double> values,
        uint seed,
        int iterations = 10_000)
    {
        if (values.Count == 0)
            return (double.NaN, double.NaN);
        var estimates = new double[iterations];
        var sample = new double[values.Count];
        uint state = seed == 0 ? 0xA341316Cu : seed;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            for (int index = 0; index < values.Count; index++)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                sample[index] = values[(int)(state % (uint)values.Count)];
            }
            estimates[iteration] = Median(sample);
        }
        return (Percentile(estimates, 0.025), Percentile(estimates, 0.975));
    }

    internal static double Spearman(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        if (x.Count != y.Count || x.Count < 2)
            return double.NaN;
        double[] xRanks = Ranks(x);
        double[] yRanks = Ranks(y);
        double xMean = xRanks.Average();
        double yMean = yRanks.Average();
        double covariance = 0;
        double xSumSquares = 0;
        double ySumSquares = 0;
        for (int index = 0; index < x.Count; index++)
        {
            double dx = xRanks[index] - xMean;
            double dy = yRanks[index] - yMean;
            covariance += dx * dy;
            xSumSquares += dx * dx;
            ySumSquares += dy * dy;
        }
        double denominator = Math.Sqrt(xSumSquares * ySumSquares);
        return denominator == 0 ? double.NaN : covariance / denominator;
    }

    internal static string ClassifyPhaseShares(IReadOnlyList<double> flushShares, IReadOnlyList<double> handleShares)
    {
        if (flushShares.Count != handleShares.Count || flushShares.Count == 0)
            return "unstable_or_inconclusive";
        bool flushDominated = flushShares.Count(value => value >= 0.70) >= 4;
        bool handleDominated = handleShares.Count(value => value >= 0.70) >= 4;
        if (flushDominated)
            return "flush_dominated";
        if (handleDominated)
            return "handle_dominated";
        bool mixed = flushShares.Count >= 4 &&
            Enumerable.Range(0, flushShares.Count).Count(index => flushShares[index] + handleShares[index] >= 0.80) >= 4;
        return mixed ? "mixed" : "unstable_or_inconclusive";
    }

    private static double[] Ranks(IReadOnlyList<double> values)
    {
        int[] order = Enumerable.Range(0, values.Count).OrderBy(index => values[index]).ToArray();
        var ranks = new double[values.Count];
        for (int start = 0; start < order.Length;)
        {
            int end = start + 1;
            while (end < order.Length && values[order[start]].Equals(values[order[end]]))
                end++;
            double rank = (start + 1 + end) / 2d;
            for (int position = start; position < end; position++)
                ranks[order[position]] = rank;
            start = end;
        }
        return ranks;
    }
}
