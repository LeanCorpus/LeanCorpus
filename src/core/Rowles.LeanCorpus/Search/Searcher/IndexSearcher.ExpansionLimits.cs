using Rowles.LeanCorpus.Search.Queries;

namespace Rowles.LeanCorpus.Search.Searcher;

public sealed partial class IndexSearcher
{
    // Validate all segments before collecting or streaming results. Immutable dictionaries
    // then keep every existing scoring and span fast path inside this admission bound.
    private void ValidateExpansionLimits(Query query, Action? checkResources = null)
        => query.Visit(new ExpansionLimitVisitor(this, checkResources));

    private Dictionary<(string Field, string Term), int>? PrecomputeWithResourceChecks(
        Query query, SearchOptions options, System.Diagnostics.Stopwatch stopwatch,
        long? deadlineTicks, CancellationToken additionalCancellation = default)
    {
        try
        {
            return PrecomputeGlobalDocFreqsForSearch(query, () =>
            {
                additionalCancellation.ThrowIfCancellationRequested();
                if (options.CancellationToken.IsCancellationRequested ||
                    (deadlineTicks.HasValue && stopwatch.ElapsedTicks > deadlineTicks.Value))
                    throw new ExpansionAdmissionInterruptedException();
            });
        }
        catch (ExpansionAdmissionInterruptedException)
        {
            return null;
        }
    }

    private sealed class ExpansionAdmissionInterruptedException : Exception;

    private sealed class ExpansionLimitVisitor(IndexSearcher searcher, Action? checkResources) : QueryVisitor
    {
        private readonly HashSet<Query> _validated = new(ReferenceEqualityComparer.Instance);

        public override void VisitLeaf(Query query)
        {
            int? maximum = query switch
            {
                WildcardQuery wildcard => wildcard.MaximumExpansions,
                PrefixQuery prefix => prefix.MaximumExpansions,
                RegexpQuery regexp => regexp.MaximumExpansions,
                _ => null
            };
            if (maximum is not int limit || !_validated.Add(query)) return;
            var budget = new MultiTermExpansionBudget(limit);
            foreach (var reader in searcher._readers)
            {
                checkResources?.Invoke();
                reader.VisitMatchingTerms(query.Field,
                    (query as PrefixQuery)?.Prefix,
                    (query as WildcardQuery)?.Pattern,
                    (query as RegexpQuery)?.CompiledRegex, budget.Admit, checkResources);
            }
        }
    }
}
