using System.Diagnostics;

namespace Rowles.LeanCorpus.Diagnostics;

/// <summary>Opt-in, trace-correlated filesystem timings. Separate from normal search telemetry.</summary>
internal static class FileSystemMeasurement
{
    internal static readonly ActivitySource Source = new("Rowles.LeanCorpus.FileSystem");

    internal static Activity? Start(string operation) => Source.StartActivity(operation);

    internal static Activity? Start(string operation, ActivityContext parentContext) =>
        Source.StartActivity(operation, ActivityKind.Internal, parentContext);

    internal static ActivityContext CaptureContext() => Source.HasListeners()
        ? Activity.Current?.Context ?? default : default;

    internal static void Record(string operation, long bytes = 0)
    {
        using var activity = Start(operation);
        activity?.SetTag("bytes", bytes);
    }
}
