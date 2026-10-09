using Microsoft.Extensions.DependencyInjection;
using OrchardCore.Indexing;
using OrchardCore.Modules;
using Rowles.LeanCorpus.OrchardCore.Search.Storage;
using Rowles.LeanCorpus.OrchardCore.Search.TestModule.Indexing;

namespace Rowles.LeanCorpus.OrchardCore.Search.TestModule;

/// <summary>Registers test-only Orchard indexing behaviour for the native vector probe.</summary>
public sealed class VectorProbeStartup : StartupBase
{
    public override void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<IContentPartIndexHandler, VectorProbeIndexHandler>();
        services.AddScoped<IContentPartIndexHandler, DifferentialFieldsIndexHandler>();
        services.AddSingleton<OrchardTestFailureInjector>();
        services.AddSingleton<ILeanCorpusFailureInjector>(provider => provider.GetRequiredService<OrchardTestFailureInjector>());
        services.AddSingleton<IOrchardTestFailureControl>(provider => provider.GetRequiredService<OrchardTestFailureInjector>());
    }
}

/// <summary>Test-host control for arming a single deterministic provider fault.</summary>
public interface IOrchardTestFailureControl
{
    string? LastTriggered { get; }
    void Arm(string point);
    void Clear();
}

internal sealed class OrchardTestFailureInjector : ILeanCorpusFailureInjector, IOrchardTestFailureControl
{
    private int _point = -1;
    private string? _lastTriggered;

    public string? LastTriggered => Volatile.Read(ref _lastTriggered);

    public void Arm(string point)
    {
        if (!Enum.TryParse(point, ignoreCase: true, out LeanCorpusFailurePoint parsed) || !Enum.IsDefined(parsed))
            throw new ArgumentException($"Unknown LeanCorpus test failure point '{point}'.", nameof(point));

        Interlocked.Exchange(ref _point, (int)parsed);
        Volatile.Write(ref _lastTriggered, null);
    }

    public void Clear()
    {
        Interlocked.Exchange(ref _point, -1);
        Volatile.Write(ref _lastTriggered, null);
    }

    public void Check(LeanCorpusFailurePoint point)
    {
        if (Interlocked.CompareExchange(ref _point, -1, (int)point) == (int)point)
        {
            Volatile.Write(ref _lastTriggered, point.ToString());
            throw new IOException($"Injected Orchard test-host failure at {point}.");
        }
    }
}
