namespace Rowles.LeanCorpus.Search;

/// <summary>Indicates that a multi-term clause exceeded its distinct dictionary-term limit.</summary>
public sealed class QueryExpansionLimitException : InvalidOperationException
{
    /// <summary>Gets the configured maximum distinct matching terms.</summary>
    public int MaximumExpansions { get; }

    /// <summary>Gets the number of the rejected distinct-term admission attempt.</summary>
    public long AttemptedExpansions { get; }

    internal QueryExpansionLimitException(int maximumExpansions)
        : base($"The query clause exceeds its expansion limit of {maximumExpansions} matching terms.")
    {
        MaximumExpansions = maximumExpansions;
        AttemptedExpansions = (long)maximumExpansions + 1;
    }
}


internal sealed class MultiTermExpansionBudget
{
    private readonly int _maximum;
    private readonly HashSet<string> _terms = new(StringComparer.Ordinal);

    internal MultiTermExpansionBudget(int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        _maximum = maximum;
    }

    internal int AcceptedTerms => _terms.Count;

    internal void Admit(string term)
    {
        if (!_terms.Contains(term) && _terms.Count == _maximum)
            throw new QueryExpansionLimitException(_maximum);
        _terms.Add(term);
    }
}
