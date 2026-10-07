using System.Text;
using System.Text.Json;
using Rowles.LeanCorpus.Store;

namespace Rowles.LeanCorpus.WindowsDurabilitySpike.Analysis;

internal static class ObservationNeutrality
{
    internal static int Validate(SpikeArguments arguments)
    {
        string output = Path.GetFullPath(arguments.Required("output"));
        string path = Path.Combine(Path.GetTempPath(), $"leancorpus-spike2-neutrality-{Guid.NewGuid():N}.bin");
        var observer = new MemoryObserver();
        var disabledCalls = new List<DurabilitySpikeOperation>();
        var enabledCalls = new List<DurabilitySpikeOperation>();
        try
        {
            File.WriteAllBytes(path, [17, 48, 79, 110, 141, 172, 203, 234]);
            using (DurabilitySpikeInstrumentation.ProbeNativeCalls(disabledCalls.Add))
                DirectoryFsync.SyncFile(path, strict: true);
            using (DurabilitySpikeInstrumentation.ProbeNativeCalls(enabledCalls.Add))
            using (DurabilitySpikeInstrumentation.Begin(observer))
                DirectoryFsync.SyncFile(path, strict: true);

            if (!disabledCalls.SequenceEqual(enabledCalls))
                throw new InvalidOperationException(
                    $"Observer-enabled native durability calls differed from the observer-disabled call sequence: " +
                    $"disabled=[{string.Join(',', disabledCalls)}], enabled=[{string.Join(',', enabledCalls)}].");
            if (observer.Count(DurabilitySpikeOperation.FilePersist) != 1)
                throw new InvalidOperationException("One wrapper persistence request did not produce exactly one wrapper event.");
            if (observer.Count(DurabilitySpikeOperation.RetryDelay) != 0)
                throw new InvalidOperationException("The controlled neutral observation unexpectedly retried.");

            DurabilitySpikeOperation[] expected = OperatingSystem.IsWindows()
                ? [DurabilitySpikeOperation.WindowsOpen, DurabilitySpikeOperation.WindowsFlush,
                    DurabilitySpikeOperation.WindowsClose]
                : [DurabilitySpikeOperation.PosixOpen, DurabilitySpikeOperation.PosixFsync,
                    DurabilitySpikeOperation.PosixClose];
            if (!disabledCalls.SequenceEqual(expected))
                throw new InvalidOperationException(
                    $"Expected one ordered native open, durability flush and close, observed [{string.Join(',', disabledCalls)}].");
            if (OperatingSystem.IsWindows() &&
                (!observer.Operations.SequenceEqual(expected) ||
                 observer.Count(DurabilitySpikeOperation.WindowsOpen) != 1 ||
                 observer.Count(DurabilitySpikeOperation.WindowsFlush) != 1 ||
                 observer.Count(DurabilitySpikeOperation.WindowsClose) != 1))
                throw new InvalidOperationException("Windows observer events do not match the existing native open/flush/close sequence.");
        }
        finally
        {
            File.Delete(path);
        }

        string jsonPath = Path.ChangeExtension(output, ".json");
        if (File.Exists(output) || File.Exists(jsonPath))
            throw new IOException("Observation-neutrality evidence already exists and will not be replaced.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var report = new
        {
            schema_version = 1,
            status = "passed",
            platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other",
            observer_enabled_native_calls = enabledCalls.Select(static operation => operation.ToString()).ToArray(),
            observer_disabled_native_calls = disabledCalls.Select(static operation => operation.ToString()).ToArray(),
            native_call_sequences_match = true,
            wrapper_persist_requests = observer.Count(DurabilitySpikeOperation.FilePersist),
            retry_delays = observer.Count(DurabilitySpikeOperation.RetryDelay),
            windows_native_call_counts = new
            {
                status = OperatingSystem.IsWindows() ? "passed" : "not_applicable_non_windows",
                create_file = enabledCalls.Count(static operation => operation == DurabilitySpikeOperation.WindowsOpen),
                flush_file_buffers = enabledCalls.Count(static operation => operation == DurabilitySpikeOperation.WindowsFlush),
                close_handle = enabledCalls.Count(static operation => operation == DurabilitySpikeOperation.WindowsClose)
            },
            posix_native_call_counts = new
            {
                status = OperatingSystem.IsWindows() ? "not_applicable_windows" : "passed",
                open = enabledCalls.Count(static operation => operation == DurabilitySpikeOperation.PosixOpen),
                fsync = enabledCalls.Count(static operation => operation == DurabilitySpikeOperation.PosixFsync),
                close = enabledCalls.Count(static operation => operation == DurabilitySpikeOperation.PosixClose)
            },
            validated_utc = DateTimeOffset.UtcNow
        };
        File.WriteAllText(output,
            "# Instrumentation audit\n\n" +
            "The same file persistence operation was run once with the observer disabled and once with it enabled. A separate in-memory probe recorded the native calls in both cases; the ordered call sequences were identical. The probe performs no filesystem operations. The observed sequence was `" +
            string.Join(" -> ", enabledCalls) + "`.\n\n" +
            "The observer received exactly one wrapper persistence event and no retry event. On Windows, the measured sequence is one CreateFileW, one FlushFileBuffers and one SafeFileHandle close. On POSIX, it is one open, one fsync and one close.\n\n" +
            "The Core observer is null by default and is scoped with AsyncLocal for the active spike operation. It receives timestamps, existing paths, native error codes, status values, file lengths already read by the commit path, and existing retry delays. It does not open, stat, enumerate, hash, read, write, flush, close, or publish files. Native call probes are dormant unless the deterministic validator installs one.\n\n" +
            "Candidate publication adds only the protocol-required GetVolumePathNameW source and destination volume-root resolution and the selected publication API call. P0 continues through FileOpenRetry.Move. P1/P2 call MoveFileExW with flags 0x09 and do not enable copy/delete fallback.\n\n" +
            "Commit checkpoints are in-memory callbacks during ordinary latency work. Process and reset fault modes may write only their control record outside the index volume, then terminate or wait without further index I/O. Raw measurement CSV and validation hashes are written after the timed operation.\n",
            new UTF8Encoding(false));
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false));
        Console.WriteLine("Observer-neutrality validation passed; audit written to " + output);
        return 0;
    }

    internal static void Require(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        if (root.GetProperty("status").GetString() != "passed" ||
            !root.GetProperty("native_call_sequences_match").GetBoolean() ||
            root.GetProperty("wrapper_persist_requests").GetInt32() != 1 ||
            root.GetProperty("retry_delays").GetInt32() != 0)
            throw new InvalidDataException("Observation-neutrality validation did not pass.");
        string nativeStatus = root.GetProperty("windows_native_call_counts").GetProperty("status").GetString() ?? "unknown";
        if (OperatingSystem.IsWindows() && nativeStatus != "passed")
            throw new InvalidDataException("Windows measurement requires a passing native call-count comparison from this Windows environment.");
        if (!OperatingSystem.IsWindows() && nativeStatus != "not_applicable_non_windows")
            throw new InvalidDataException("The Windows native call-count validation record has an unexpected platform status.");
    }

    private sealed class MemoryObserver : IDurabilitySpikeObserver
    {
        private readonly List<DurabilitySpikeOperation> _operations = [];
        public bool SyncDirectoryAfterMarkerPublication => true;
        internal IReadOnlyList<DurabilitySpikeOperation> Operations => _operations;
        public void OnEvent(in DurabilitySpikeEvent value) => _operations.Add(value.Operation);
        public void OnCheckpoint(DurabilitySpikeCheckpoint checkpoint, string? path) { }
        public void BeforeDurabilityOperation(string operationId) { }
        public void AfterDurabilityOperation(string operationId) { }
        public DirtyFileTracker.DirtyFile PublishMarker(string temporaryPath, string destinationPath, bool overwrite)
            => throw new InvalidOperationException("Neutrality validation does not publish markers.");
        internal int Count(DurabilitySpikeOperation operation)
            => _operations.Count(value => value == operation);
    }
}
