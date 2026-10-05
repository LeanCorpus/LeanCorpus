namespace Rowles.LeanCorpus.Diagnostics;

/// <summary>Branch-only observation points used by the compound durability spike.</summary>
internal enum SpikeInstrumentationPoint
{
    LooseSegmentComplete,
    PackStarted,
    PackMember,
    PackInputBytesCopied,
    PackSourceBytesRead,
    PackTempClosed,
    PackPublished,
    PackCompleted,
    FileRenamed,
    FileDeleted,
    FilePersistRequested,
    FilePersistReturned,
    DirectoryPersistRequested,
    DirectoryPersistReturned,
    DurabilityCandidates,
    ChangedFilesPersisted,
    CommitMetadataStarting,
    CommitMetadataPrepared,
    CommitMarkerTempPersisted,
    CommitMarkerRenamed,
    CommitDirectoryPersistReturned,
    Checkpoint,
    AtomicReplace
}

/// <summary>One branch-only observation of an existing persistence operation.</summary>
internal readonly record struct SpikeInstrumentationEvent(
    SpikeInstrumentationPoint Point,
    string? Path,
    long Value = 0,
    long Amount = 0,
    string? Detail = null);

/// <summary>
/// Disabled by default. The spike harness attaches a synchronous observer in its
/// own process so the production call order and durability semantics remain intact.
/// </summary>
internal static class SpikeInstrumentation
{
    private static Action<SpikeInstrumentationEvent>? s_observer;

    internal static bool IsObserving => Volatile.Read(ref s_observer) is not null;

    internal static Action<SpikeInstrumentationEvent>? Observer
    {
        get => Volatile.Read(ref s_observer);
        set => Volatile.Write(ref s_observer, value);
    }

    internal static void Record(
        SpikeInstrumentationPoint point,
        string? path = null,
        long value = 0,
        long amount = 0,
        string? detail = null)
        => Volatile.Read(ref s_observer)?.Invoke(new SpikeInstrumentationEvent(point, path, value, amount, detail));

    internal static void Checkpoint(string name)
        => Record(SpikeInstrumentationPoint.Checkpoint, name);
}
