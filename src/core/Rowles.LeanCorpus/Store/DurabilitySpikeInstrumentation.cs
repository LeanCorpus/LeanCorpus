using System.Threading;

namespace Rowles.LeanCorpus.Store;

/// <summary>Operations observed by the branch-local Windows durability spike.</summary>
internal enum DurabilitySpikeOperation
{
    WindowsOpen,
    WindowsFlush,
    WindowsClose,
    PosixOpen,
    PosixFsync,
    PosixClose,
    FilePersist,
    DirectoryPersist,
    DurabilityCandidateFile,
    DurabilityCandidateBytes,
    RetryDelay,
    CommitFlushStage,
    ChangedFileSyncStage,
    PrePublicationDirectorySync,
    MarkerWrite,
    MarkerFlush,
    MarkerPublicationApiCall,
    CandidateVolumeCheck,
    MarkerPublication,
    PostPublicationDirectorySync,
    CompoundCopy,
    CompoundClose,
    CompoundRename,
    CompoundLooseDelete,
    CompoundPack
}

/// <summary>Commit boundaries used by branch-local durability and crash experiments.</summary>
internal enum DurabilitySpikeCheckpoint
{
    AfterSegmentFilesComplete,
    AfterDataFilesPersisted,
    AfterCommitMarkerTempWritten,
    AfterCommitMarkerTempPersisted,
    BeforePublicationCall,
    AfterPublicationCall,
    AfterDirectoryPersistAttempt,
    AfterCommitReturn,
    AfterCompoundTempCloseBeforeRename,
    AfterCompoundRename,
    AfterLooseMembersDeleted
}

/// <summary>One event containing timestamps and values already available to the operation.</summary>
internal readonly struct DurabilitySpikeEvent
{
    internal DurabilitySpikeEvent(
        DurabilitySpikeOperation operation,
        string? path,
        long startedAt,
        long completedAt,
        int errorCode = 0,
        bool succeeded = true,
        long value = 0,
        int auxiliaryValue = 0)
    {
        Operation = operation;
        Path = path;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        ErrorCode = errorCode;
        Succeeded = succeeded;
        Value = value;
        AuxiliaryValue = auxiliaryValue;
    }

    internal DurabilitySpikeOperation Operation { get; }
    internal string? Path { get; }
    internal long StartedAt { get; }
    internal long CompletedAt { get; }
    internal int ErrorCode { get; }
    internal bool Succeeded { get; }
    internal long Value { get; }
    internal int AuxiliaryValue { get; }
}

/// <summary>Receives branch-local events and selects the marker publication candidate.</summary>
internal interface IDurabilitySpikeObserver
{
    bool SyncDirectoryAfterMarkerPublication { get; }

    void OnEvent(in DurabilitySpikeEvent value);

    void OnCheckpoint(DurabilitySpikeCheckpoint checkpoint, string? path);

    void BeforeDurabilityOperation(string operationId);

    void AfterDurabilityOperation(string operationId);

    DirtyFileTracker.DirtyFile PublishMarker(string temporaryPath, string destinationPath, bool overwrite);
}

/// <summary>
/// Dormant-by-default, per-async-flow instrumentation used only by the Spike 2 branch.
/// It records existing operations and never performs filesystem inspection itself.
/// </summary>
internal static class DurabilitySpikeInstrumentation
{
    private static readonly AsyncLocal<IDurabilitySpikeObserver?> CurrentObserver = new();
    private static readonly AsyncLocal<bool> MarkerPublicationCall = new();
    private static readonly AsyncLocal<Action<DurabilitySpikeOperation>?> NativeCallProbe = new();

    internal static IDurabilitySpikeObserver? Current => CurrentObserver.Value;

    internal static bool IsMarkerPublicationCall => MarkerPublicationCall.Value;

    internal static IDisposable ProbeNativeCalls(Action<DurabilitySpikeOperation> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Action<DurabilitySpikeOperation>? previous = NativeCallProbe.Value;
        NativeCallProbe.Value = record;
        return new NativeProbeScope(previous);
    }

    internal static void RecordNativeCall(DurabilitySpikeOperation operation)
        => NativeCallProbe.Value?.Invoke(operation);

    internal static IDisposable Begin(IDurabilitySpikeObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        IDurabilitySpikeObserver? previous = CurrentObserver.Value;
        CurrentObserver.Value = observer;
        return new Scope(previous);
    }

    internal static void Record(in DurabilitySpikeEvent value)
        => CurrentObserver.Value?.OnEvent(value);

    internal static void Checkpoint(DurabilitySpikeCheckpoint checkpoint, string? path = null)
        => CurrentObserver.Value?.OnCheckpoint(checkpoint, path);

    internal static void BeforeDurabilityOperation(string operationId)
        => CurrentObserver.Value?.BeforeDurabilityOperation(operationId);

    internal static void AfterDurabilityOperation(string operationId)
        => CurrentObserver.Value?.AfterDurabilityOperation(operationId);

    internal static DirtyFileTracker.DirtyFile PublishMarker(
        string temporaryPath,
        string destinationPath,
        bool overwrite)
    {
        IDurabilitySpikeObserver? observer = CurrentObserver.Value;
        if (observer is null)
            return FileOpenRetry.Move(temporaryPath, destinationPath, overwrite);

        bool previous = MarkerPublicationCall.Value;
        MarkerPublicationCall.Value = true;
        try
        {
            return observer.PublishMarker(temporaryPath, destinationPath, overwrite);
        }
        finally
        {
            MarkerPublicationCall.Value = previous;
        }
    }

    private sealed class Scope(IDurabilitySpikeObserver? previous) : IDisposable
    {
        private IDurabilitySpikeObserver? _previous = previous;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            CurrentObserver.Value = _previous;
            _previous = null;
            _disposed = true;
        }
    }

    private sealed class NativeProbeScope(Action<DurabilitySpikeOperation>? previous) : IDisposable
    {
        private Action<DurabilitySpikeOperation>? _previous = previous;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            NativeCallProbe.Value = _previous;
            _previous = null;
            _disposed = true;
        }
    }
}
