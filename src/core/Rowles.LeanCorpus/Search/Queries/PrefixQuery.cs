namespace Rowles.LeanCorpus.Search.Queries;

/// <summary>
/// Matches all terms starting with a given prefix.
/// </summary>
public sealed class PrefixQuery : Query
{
    /// <inheritdoc/>
    public override string Field { get; }

    /// <summary>Gets the prefix that all matched terms must start with.</summary>
    public string Prefix { get; }

    /// <summary>Initialises a new <see cref="PrefixQuery"/> for the given field and prefix.</summary>
    /// <param name="field">The field to search.</param>
    /// <param name="prefix">The prefix string that matching terms must begin with.</param>
    public PrefixQuery(string field, string prefix) : this(field, prefix, null)
    {
    }

    /// <summary>Initialises a query with an optional distinct matched-term expansion limit.</summary>
    /// <param name="field">The field to search.</param>
    /// <param name="prefix">The matching prefix.</param>
    /// <param name="maximumExpansions">A positive per-clause limit, or null for trusted unbounded execution.</param>
    public PrefixQuery(string field, string prefix, int? maximumExpansions)
    {
        if (maximumExpansions is <= 0) throw new ArgumentOutOfRangeException(nameof(maximumExpansions));
        MaximumExpansions = maximumExpansions;
        Field = field;
        Prefix = prefix;
    }

    /// <summary>Gets the distinct matched-term limit, or null for trusted unbounded execution.</summary>
    public int? MaximumExpansions { get; }

    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        obj is PrefixQuery other &&
        string.Equals(Field, other.Field, StringComparison.Ordinal) &&
        string.Equals(Prefix, other.Prefix, StringComparison.Ordinal) &&
        MaximumExpansions == other.MaximumExpansions &&
        Boost == other.Boost;

    /// <inheritdoc/>
    public override int GetHashCode() => CombineBoost(HashCode.Combine(nameof(PrefixQuery), Field, Prefix, MaximumExpansions));
}
