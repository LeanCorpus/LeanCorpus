namespace Rowles.LeanCorpus.OrchardCore.Search.Models;

/// <summary>Provider-specific lexical search settings for an Orchard index profile.</summary>
public sealed class LeanCorpusIndexMetadata
{
    /// <summary>Gets or sets the default LeanCorpus analyser name.</summary>
    public string DefaultAnalyser { get; set; } = "standard";

    /// <summary>Gets or sets the Orchard fields used by generic site search.</summary>
    public string[] SearchFields { get; set; } = [];
}
