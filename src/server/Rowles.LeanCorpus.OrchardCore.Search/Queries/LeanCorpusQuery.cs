namespace Rowles.LeanCorpus.OrchardCore.Search.Queries;

internal sealed record LeanCorpusQuery(
    string Index,
    System.Text.Json.JsonElement Query,
    int Skip,
    int Take);
