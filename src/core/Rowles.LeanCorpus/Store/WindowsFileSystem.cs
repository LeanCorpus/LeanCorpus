using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rowles.LeanCorpus.Store;

/// <summary>Windows filesystem operations implemented directly over Win32 handles.</summary>
internal sealed partial class WindowsFileSystem : IPlatformFileSystem
{
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorDeletePending = 303;

    private readonly ConcurrentDictionary<string, DirectoryCapabilityState> _directoryCapabilities =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, bool, DirectorySyncResult>? _syncHandleForTesting;

    internal static readonly WindowsFileSystem Instance = new();

    private WindowsFileSystem() { }

    internal WindowsFileSystem(Func<string, bool, DirectorySyncResult> syncHandleForTesting)
    {
        _syncHandleForTesting = syncHandleForTesting;
    }

    public void SyncFile(string path) => _ = Sync(path, isDirectory: false);

    public DirectorySyncResult SyncDirectory(string path)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
        var state = _directoryCapabilities.GetOrAdd(root, static _ => new DirectoryCapabilityState());
        if (Volatile.Read(ref state.Unsupported) != 0)
            return DirectorySyncResult.SkippedUnsupported;

        DirectorySyncResult result = Sync(path, isDirectory: true);
        if (result == DirectorySyncResult.Unsupported)
            Volatile.Write(ref state.Unsupported, 1);
        return result;
    }

    public bool IsTransient(Exception exception)
    {
        if (exception is not IOException and not UnauthorizedAccessException)
            return false;

        int error = exception.HResult & 0xFFFF;
        return error is ErrorSharingViolation or ErrorLockViolation or ErrorDeletePending;
    }

    private DirectorySyncResult Sync(string path, bool isDirectory) =>
        _syncHandleForTesting is null
            ? SyncHandle(path, isDirectory)
            : _syncHandleForTesting(path, isDirectory);

    private static DirectorySyncResult SyncHandle(string path, bool isDirectory)
    {
        IDurabilitySpikeObserver? observer = DurabilitySpikeInstrumentation.Current;
        long openStartedAt = observer is null ? 0 : Stopwatch.GetTimestamp();
        DurabilitySpikeInstrumentation.RecordNativeCall(DurabilitySpikeOperation.WindowsOpen);
        SafeFileHandle handle = CreateFileW(
            path,
            GenericWrite,
            FileShareRead | FileShareWrite | FileShareDelete,
            0,
            OpenExisting,
            isDirectory ? FileFlagBackupSemantics : 0,
            0);
        int openError = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        if (observer is not null)
        {
            long openedAt = Stopwatch.GetTimestamp();
            observer.OnEvent(new DurabilitySpikeEvent(
                DurabilitySpikeOperation.WindowsOpen, path, openStartedAt, openedAt,
                openError, !handle.IsInvalid));
        }

        try
        {
            if (handle.IsInvalid)
                ThrowWin32(path, isDirectory, openError);

            long flushStartedAt = observer is null ? 0 : Stopwatch.GetTimestamp();
            if (!handle.IsInvalid)
                DurabilitySpikeInstrumentation.RecordNativeCall(DurabilitySpikeOperation.WindowsFlush);
            bool flushed = FlushFileBuffers(handle);
            int flushError = flushed ? 0 : Marshal.GetLastWin32Error();
            if (observer is not null)
            {
                long flushedAt = Stopwatch.GetTimestamp();
                observer.OnEvent(new DurabilitySpikeEvent(
                    DurabilitySpikeOperation.WindowsFlush, path, flushStartedAt, flushedAt,
                    flushError, flushed));
            }

            if (flushed)
                return DirectorySyncResult.Succeeded;

            if (isDirectory && flushError == ErrorAccessDenied)
                return DirectorySyncResult.Unsupported;

            ThrowWin32(path, isDirectory, flushError);
            return DirectorySyncResult.Succeeded;
        }
        finally
        {
            long closeStartedAt = observer is null ? 0 : Stopwatch.GetTimestamp();
            bool closeWasValid = !handle.IsInvalid;
            if (closeWasValid)
                DurabilitySpikeInstrumentation.RecordNativeCall(DurabilitySpikeOperation.WindowsClose);
            handle.Dispose();
            if (observer is not null)
            {
                long closedAt = Stopwatch.GetTimestamp();
                observer.OnEvent(new DurabilitySpikeEvent(
                    DurabilitySpikeOperation.WindowsClose, path, closeStartedAt, closedAt,
                    succeeded: closeWasValid));
            }
        }
    }

    private static void ThrowWin32(string path, bool isDirectory, int error)
    {
        string kind = isDirectory ? "directory" : "file";
        throw error switch
        {
            2 => new FileNotFoundException($"File not found: '{path}'.", path),
            3 => new DirectoryNotFoundException($"Directory path not found: '{path}'."),
            ErrorAccessDenied => new UnauthorizedAccessException($"Access denied to {kind} '{path}'."),
            _ => new IOException($"Unable to synchronise {kind} '{path}': {new Win32Exception(error).Message} (0x{error:x8}).", error)
        };
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushFileBuffers(SafeFileHandle handle);

    private sealed class DirectoryCapabilityState
    {
        internal int Unsupported;
    }
}
