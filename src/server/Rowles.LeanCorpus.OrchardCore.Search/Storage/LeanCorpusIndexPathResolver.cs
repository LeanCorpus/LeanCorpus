using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using OrchardCore.Environment.Shell;

namespace Rowles.LeanCorpus.OrchardCore.Search.Storage;

internal sealed record LeanCorpusIndexPaths(
    string IndexFullName,
    string TenantDirectory,
    string IndexDirectory,
    string SchemaPath,
    string StatePath);

/// <summary>Resolves tenant-local provider storage without trusting Orchard index names as paths.</summary>
internal sealed class LeanCorpusIndexPathResolver
{
    private readonly string _contentRoot;
    private readonly string _tenantName;

    public LeanCorpusIndexPathResolver(IHostEnvironment environment, ShellSettings shellSettings)
        : this(environment.ContentRootPath, shellSettings.Name)
    {
    }

    internal LeanCorpusIndexPathResolver(string contentRoot, string tenantName)
    {
        _contentRoot = Path.GetFullPath(contentRoot);
        _tenantName = tenantName;
    }

    public LeanCorpusIndexPaths Resolve(string indexFullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexFullName);
        string tenant = SafeKey(_tenantName);
        string index = SafeKey(indexFullName);
        string tenantDirectory = Path.Combine(_contentRoot, "App_Data", "Sites", tenant, "LeanCorpus");
        return new LeanCorpusIndexPaths(
            indexFullName,
            tenantDirectory,
            Path.Combine(tenantDirectory, "indexes", index),
            Path.Combine(tenantDirectory, "schemas", index + ".json"),
            Path.Combine(tenantDirectory, "state", index + ".json"));
    }

    public static string SafeKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var slug = new StringBuilder(Math.Min(value.Length, 48));
        foreach (char character in value)
        {
            if (slug.Length == 48)
                break;
            slug.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_');
        }

        string readable = slug.ToString().Trim('_', '-');
        if (readable.Length == 0)
            readable = "index";

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..12];
        return $"{readable}-{hash}";
    }
}
