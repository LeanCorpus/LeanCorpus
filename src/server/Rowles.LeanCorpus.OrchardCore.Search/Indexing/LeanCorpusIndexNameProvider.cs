using OrchardCore.Indexing;
using OrchardCore.Environment.Shell;

namespace Rowles.LeanCorpus.OrchardCore.Search.Indexing;

/// <summary>Creates deterministic tenant-local physical index names.</summary>
public sealed class LeanCorpusIndexNameProvider : IIndexNameProvider
{
    private readonly string _tenantName;

    /// <summary>Initialises the naming provider for the active Orchard tenant.</summary>
    public LeanCorpusIndexNameProvider(ShellSettings shellSettings)
        : this(shellSettings.Name)
    {
    }

    internal LeanCorpusIndexNameProvider(string tenantName)
    {
        _tenantName = tenantName;
    }

    public string GetFullIndexName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return $"{_tenantName}/{name.Trim()}";
    }
}
