using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Rowles.LeanCorpus.Store;
using Rowles.LeanCorpus.WindowsDurabilitySpike.Analysis;

namespace Rowles.LeanCorpus.WindowsDurabilitySpike.Mechanisms;

internal static class MechanismRunner
{
    private static readonly long[] PayloadSizes = [8L * 1024 * 1024, 82L * 1024 * 1024];
    private static readonly int[] LocalFileCounts = [1, 4, 16, 64, 128];
    private static readonly int[] HostedFileCounts = [1, 16, 64, 128];
    private static readonly string[] NativeVariants =
    [
        "A_current_full",
        "B_open_close_only",
        "C_flush_only_preopened",
        "D_leancorpus_wrapper"
    ];

    internal static int PrepareOrder(SpikeArguments arguments)
    {
        string output = Path.GetFullPath(arguments.Required("output"));
        string profile = arguments.Optional("profile", "local");
        string environment = arguments.Optional("environment", "windows");
        if (profile is not "local" and not "hosted")
            throw new ArgumentException("--profile must be local or hosted.");
        string[] variants = environment == "ubuntu"
            ? ["D_leancorpus_wrapper"]
            : NativeVariants;
        var rows = new List<MechanismOrderRow>();
        int launchCount = profile == "local" ? 5 : 3;
        int[] fileCounts = profile == "local" ? LocalFileCounts : HostedFileCounts;

        for (int launch = 1; launch <= launchCount; launch++)
        {
            uint shuffleSeed = (uint)((profile == "hosted" ? 20261320 : 20261300) + launch);
            var launchRows = new List<MechanismOrderRow>();
            foreach (long payloadBytes in PayloadSizes)
            foreach (int fileCount in fileCounts)
            foreach (string variant in variants)
            {
                int sampleCount = profile == "local" ? 5 : 3;
                for (int observation = 0; observation <= sampleCount; observation++)
                    launchRows.Add(new MechanismOrderRow(
                        $"primary-{launch}", launch,
                        shuffleSeed.ToString(System.Globalization.CultureInfo.InvariantCulture), shuffleSeed,
                        payloadBytes, fileCount, variant, observation, observation == 0, 0));
            }
            SeededShuffle.Shuffle(launchRows, shuffleSeed);
            for (int index = 0; index < launchRows.Count; index++)
                launchRows[index] = launchRows[index] with { OrderIndex = index };
            rows.AddRange(launchRows);

            if (profile == "local" && launch <= 3)
            {
                uint launchSeed = (uint)(20261310 + launch);
                var cleanRows = new List<MechanismOrderRow>();
                foreach (long payloadBytes in PayloadSizes)
                foreach (int fileCount in fileCounts)
                for (int observation = 0; observation <= 3; observation++)
                    cleanRows.Add(new MechanismOrderRow(
                        $"clean-{launch}", launch, launchSeed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        shuffleSeed, payloadBytes, fileCount, "E_clean_reflush_preopened",
                        observation, observation == 0, 0));
                SeededShuffle.Shuffle(cleanRows, shuffleSeed ^ 0x9E3779B9u);
                for (int index = 0; index < cleanRows.Count; index++)
                    cleanRows[index] = cleanRows[index] with { OrderIndex = index };
                rows.AddRange(cleanRows);
            }
        }

        using var csv = new CsvFile(output,
            "launch_group", "launch_index", "launch_seed", "shuffle_seed", "order_index",
            "payload_bytes", "file_count", "variant", "observation_index", "warm_up");
        foreach (MechanismOrderRow row in rows)
            csv.WriteRow(row.LaunchGroup, row.LaunchIndex, row.LaunchSeed, row.ShuffleSeed,
                row.OrderIndex, row.PayloadBytes, row.FileCount, row.Variant,
                row.ObservationIndex, row.IsWarmUp);
        Console.WriteLine($"Wrote {rows.Count} planned observations to {output}");
        return 0;
    }

    internal static int RunLaunch(SpikeArguments arguments)
    {
        string profile = arguments.Optional("profile", "local");
        string environment = arguments.Optional("environment-class", "local_windows_vm");
        int launch = arguments.RequiredInt("launch");
        string dataRoot = Path.GetFullPath(arguments.Required("data-root"));
        string evidence = Path.GetFullPath(arguments.Required("evidence"));
        string orderPath = Path.GetFullPath(arguments.Required("order"));
        ObservationNeutrality.Require(Path.GetFullPath(arguments.Required("neutrality-validation")));
        EnsureEvidenceOutsideDataRoot(dataRoot, evidence);
        bool ubuntu = environment == "hosted_ubuntu_24_04";
        if (!ubuntu && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows-native mechanism cells require Windows.");
        if (profile == "hosted" && launch is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(launch), "Hosted launches are numbered 1 through 3.");
        if (profile == "local" && launch is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(launch), "Local launches are numbered 1 through 5.");

        string group = $"primary-{launch}";
        string cleanGroup = $"clean-{launch}";
        List<MechanismOrderRow> orderRows = ReadOrder(orderPath)
            .Where(row => row.LaunchGroup == group || row.LaunchGroup == cleanGroup)
            .OrderBy(static row => row.OrderIndex)
            .ToList();
        if (orderRows.Count == 0)
            throw new InvalidDataException($"No planned observations for launch {launch} in '{orderPath}'.");

        string launchEvidence = Path.Combine(evidence, $"launch-{launch}");
        if (Directory.Exists(launchEvidence))
            throw new IOException($"Launch evidence already exists and will not be replaced: {launchEvidence}");
        Directory.CreateDirectory(launchEvidence);
        EnvironmentSnapshot.Write(launchEvidence, environment, dataRoot, orderPath, launch);

        var observations = new List<MechanismObservation>(orderRows.Count);
        var fileRows = new List<MechanismFileRow>(orderRows.Count * 4);
        var cleanRows = new List<CleanReflushRow>();
        var successfulTrialDirectories = new List<string>();
        var wrapperObserver = new MechanismObserver();

        foreach (MechanismOrderRow order in orderRows)
        {
            string trialPath = Path.Combine(dataRoot,
                $"spike2-{environment}-launch{launch}-{order.Variant}-p{order.PayloadBytes}-n{order.FileCount}-o{order.ObservationIndex}");
            var observation = new MechanismObservation
            {
                Environment = environment,
                LaunchIndex = launch,
                LaunchSeed = order.LaunchSeed,
                ShuffleSeed = order.ShuffleSeed,
                PayloadBytes = order.PayloadBytes,
                FileCount = order.FileCount,
                Variant = order.Variant,
                ObservationIndex = order.ObservationIndex,
                IsWarmUp = order.IsWarmUp,
                OrderIndex = order.OrderIndex,
                TrialDirectory = trialPath,
                Success = false
            };
            observations.Add(observation);

            try
            {
                if (Directory.Exists(trialPath))
                    throw new IOException($"Trial directory already exists; observations are never replaced: {trialPath}");
                Directory.CreateDirectory(trialPath);
                List<PayloadFile> files = WritePayloadFiles(
                    trialPath, order.PayloadBytes, order.FileCount, out long timestampAfterLastClose,
                    out string payloadSha256);
                observation.TimestampAfterLastClose = timestampAfterLastClose;
                observation.PayloadSha256 = payloadSha256;

                if (order.Variant == "E_clean_reflush_preopened")
                    RunCleanReflush(files, observation, cleanRows);
                else
                    RunMechanism(files, observation, fileRows, wrapperObserver);

                observation.FileSizes = string.Join(';', files.Select(static file => file.Length));
                observation.ExpectedHashes = string.Join(';', files.Select(static file => file.ExpectedSha256));
                observation.Success = observation.OperationSuccess;
                observation.HashValidation = ValidateFiles(files);
                observation.Success &= observation.HashValidation;
                observation.FileSizesValidated = files.All(static file => file.Length == file.ExpectedLength);
                observation.Success &= observation.FileSizesValidated;
                if (observation.Success)
                    successfulTrialDirectories.Add(trialPath);
                else
                    observation.Error = observation.Error ?? "operation_or_content_validation_failed";
            }
            catch (Exception ex)
            {
                observation.Error = $"{ex.GetType().Name}: {ex.Message}";
                observation.Success = false;
                observation.HashValidation = false;
            }
        }

        WriteMechanismCsv(Path.Combine(launchEvidence, "windows-file-mechanism.csv"), observations);
        WriteMechanismFileCsv(Path.Combine(launchEvidence, "windows-file-mechanism-per-file.csv"), fileRows);
        WriteCleanCsv(Path.Combine(launchEvidence, "windows-clean-reflush.csv"), cleanRows);
        foreach (string trialPath in successfulTrialDirectories)
            Directory.Delete(trialPath, recursive: true);

        int failed = observations.Count(static observation => !observation.Success);
        Console.WriteLine($"Launch {launch}: {observations.Count} attempts, {failed} failed. Evidence: {launchEvidence}");
        return failed == 0 ? 0 : 1;
    }

    internal static int TraceCell(SpikeArguments arguments)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Mechanism ETW traces require Windows.");
        string variant = arguments.Required("variant");
        if (variant is not "A_current_full" and not "D_leancorpus_wrapper")
            throw new ArgumentException("Mechanism traces use A_current_full or D_leancorpus_wrapper.");
        long payloadBytes = checked((long)arguments.RequiredUlong("payload-bytes"));
        if (payloadBytes is not (8L * 1024 * 1024) and not (82L * 1024 * 1024))
            throw new ArgumentOutOfRangeException("payload-bytes", "Trace payloads must be 8 MiB or 82 MiB.");
        int fileCount = arguments.RequiredInt("file-count");
        if (fileCount is not 16 and not 64)
            throw new ArgumentOutOfRangeException("file-count", "Trace file counts must be 16 or 64.");

        string dataRoot = Path.GetFullPath(arguments.Required("data-root"));
        string evidence = Path.GetFullPath(arguments.Required("evidence"));
        ObservationNeutrality.Require(Path.GetFullPath(arguments.Required("neutrality-validation")));
        EnsureEvidenceOutsideDataRoot(dataRoot, evidence);
        if (Directory.Exists(evidence) || Directory.Exists(dataRoot))
            throw new IOException("Mechanism trace data and evidence directories must be new.");
        Directory.CreateDirectory(evidence);
        Directory.CreateDirectory(dataRoot);

        string cellId = $"mechanism-{variant}-{payloadBytes / (1024 * 1024)}MiB-{fileCount}";
        string orderPath = Path.Combine(evidence, "trace-order.csv");
        using (var order = new CsvFile(orderPath, "payload_bytes", "file_count", "variant", "observation_index", "warm_up"))
            order.WriteRow(payloadBytes, fileCount, variant, 1, false);
        EnvironmentSnapshot.Write(evidence, "local_windows_vm", dataRoot, orderPath, 0);

        string trialPath = Path.Combine(dataRoot, "spike2-" + cellId);
        if (Directory.Exists(trialPath))
            throw new IOException($"Mechanism trace trial already exists: {trialPath}");
        Directory.CreateDirectory(trialPath);
        List<PayloadFile> files = WritePayloadFiles(trialPath, payloadBytes, fileCount,
            out long timestampAfterLastClose, out string payloadSha256);
        var observation = new MechanismObservation
        {
            Environment = "local_windows_vm",
            LaunchIndex = 0,
            LaunchSeed = "0",
            ShuffleSeed = 0,
            PayloadBytes = payloadBytes,
            FileCount = fileCount,
            Variant = variant,
            ObservationIndex = 1,
            IsWarmUp = false,
            OrderIndex = 0,
            TrialDirectory = trialPath,
            TimestampAfterLastClose = timestampAfterLastClose,
            PayloadSha256 = payloadSha256
        };
        var fileRows = new List<MechanismFileRow>(fileCount);
        var observer = new MechanismObserver(captureEvents: true);
        using (DurabilitySpikeInstrumentation.Begin(observer))
        using (DurabilitySpikeInstrumentation.ProbeNativeCalls(observer.RecordNativeCall))
            RunMechanism(files, observation, fileRows, observer);
        observation.Success = observation.OperationSuccess && ValidateFiles(files) &&
                              files.All(static file => file.Length == file.ExpectedLength);

        DurabilitySpikeOperation[] nativeSequence = observer.NativeCalls.ToArray();
        DurabilitySpikeOperation[] expected = Enumerable.Range(0, fileCount)
            .SelectMany(static _ => new[] { DurabilitySpikeOperation.WindowsOpen,
                DurabilitySpikeOperation.WindowsFlush, DurabilitySpikeOperation.WindowsClose }).ToArray();
        bool ordered = nativeSequence.SequenceEqual(expected);
        string callSummary = $"{fileCount} x CreateFileW -> FlushFileBuffers -> SafeFileHandle close";
        File.WriteAllText(Path.Combine(evidence, "trace-cell-events.json"),
            JsonSerializer.Serialize(observer.Events.Select(value => new
            {
                operation = value.Operation.ToString(), path = value.Path,
                started_at = value.StartedAt, completed_at = value.CompletedAt,
                error_code = value.ErrorCode, succeeded = value.Succeeded,
                value = value.Value, auxiliary_value = value.AuxiliaryValue
            }).ToArray(), new JsonSerializerOptions { WriteIndented = true }) + "\n", new System.Text.UTF8Encoding(false));
        using (var filesCsv = new CsvFile(Path.Combine(evidence, "windows-file-mechanism-per-file.csv"),
                   "file_index", "file_bytes", "open_ms", "flush_ms", "close_ms", "full_ms",
                   "open_success", "flush_success", "close_success", "win32_error", "wrapper_call_ms"))
        {
            foreach (MechanismFileRow row in fileRows)
                filesCsv.WriteRow(row.FileIndex, row.FileBytes, TicksToMs(row.OpenTicks),
                    TicksToMs(row.FlushTicks), TicksToMs(row.CloseTicks), TicksToMs(row.FullTicks),
                    row.OpenSuccess, row.FlushSuccess, row.CloseSuccess, row.Win32Error,
                    TicksToMs(row.WrapperCallTicks));
        }
        File.WriteAllText(Path.Combine(evidence, "trace-cell.json"),
            JsonSerializer.Serialize(new
            {
                schema_version = 1,
                cell_id = cellId,
                trace_type = "mechanism",
                candidate_id = variant,
                representation = $"{payloadBytes / (1024 * 1024)}MiB-{fileCount}",
                payload_bytes = payloadBytes,
                file_count = fileCount,
                payload_sha256 = payloadSha256,
                per_file_sizes = files.Select(static file => file.Length).ToArray(),
                expected_create_file_count = fileCount,
                expected_flush_filebuffers_count = fileCount,
                observed_call_sequence_summary = callSummary,
                observed_native_call_sequence = nativeSequence.Select(NativeCallName).ToArray(),
                event_order_passed = ordered && observation.Success,
                observation_success = observation.Success,
                variant,
                experiment_sha = Environment.GetEnvironmentVariable("SPIKE_EXPERIMENT_SHA") ?? "unknown",
                generated_utc = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true }) + "\n", new System.Text.UTF8Encoding(false));
        Console.WriteLine($"Mechanism trace cell {cellId}: observed {(ordered ? "the expected native call order" : "an unexpected native call order")}.");
        return ordered && observation.Success ? 0 : 1;
    }

    private static void RunMechanism(
        IReadOnlyList<PayloadFile> files,
        MechanismObservation observation,
        List<MechanismFileRow> rows,
        MechanismObserver wrapperObserver)
    {
        observation.OperationSuccess = true;
        long operationStartedAt = 0;
        switch (observation.Variant)
        {
            case "A_current_full":
            case "B_open_close_only":
                operationStartedAt = Stopwatch.GetTimestamp();
                observation.WriteToSyncGapMs = Milliseconds(observation.TimestampAfterLastClose, operationStartedAt);
                foreach (PayloadFile file in files)
                    RunOpenOne(file, observation.Variant == "A_current_full", observation, rows);
                observation.TotalMs = Milliseconds(operationStartedAt, Stopwatch.GetTimestamp());
                break;

            case "C_flush_only_preopened":
                var handles = new List<(PayloadFile File, SafeFileHandle? Handle, long OpenMs, int Error)>();
                observation.WriteToSyncGapMs = Milliseconds(
                    observation.TimestampAfterLastClose, Stopwatch.GetTimestamp());
                foreach (PayloadFile file in files)
                {
                    SafeFileHandle? handle = TryOpen(file.Path, out long openTicks, out int error);
                    handles.Add((file, handle, openTicks, error));
                    observation.OperationSuccess &= handle is not null;
                }
                operationStartedAt = Stopwatch.GetTimestamp();
                var flushResults = new List<(bool Success, int Error, long Ticks)>(handles.Count);
                foreach ((PayloadFile _, SafeFileHandle? handle, _, int openError) in handles)
                {
                    long flushStart = Stopwatch.GetTimestamp();
                    bool flushSuccess = false;
                    int error = openError;
                    long flushEnd = flushStart;
                    if (handle is not null)
                    {
                        flushSuccess = NativeFileOperations.Flush(handle, out error);
                        flushEnd = Stopwatch.GetTimestamp();
                    }
                    flushResults.Add((flushSuccess, error, flushEnd - flushStart));
                    observation.OperationSuccess &= flushSuccess;
                }
                long flushLoopCompletedAt = Stopwatch.GetTimestamp();
                observation.TotalMs = Milliseconds(operationStartedAt, flushLoopCompletedAt);
                for (int index = 0; index < handles.Count; index++)
                {
                    (PayloadFile file, SafeFileHandle? handle, long openTicks, int openError) = handles[index];
                    (bool flushSuccess, int error, long flushTicks) = flushResults[index];
                    long closeStart = Stopwatch.GetTimestamp();
                    bool closeSuccess = handle is not null;
                    if (handle is not null)
                    {
                        try { NativeFileOperations.Close(handle); }
                        catch { closeSuccess = false; }
                    }
                    long closeEnd = Stopwatch.GetTimestamp();
                    observation.OperationSuccess &= flushSuccess && closeSuccess;
                    rows.Add(new MechanismFileRow(
                        observation, file.Index, file.Length,
                        openTicks, flushTicks, closeEnd - closeStart,
                        openTicks + flushTicks + closeEnd - closeStart,
                        openError == 0, flushSuccess, closeSuccess, error,
                        FlushAttempted: handle is not null,
                        WrapperCallTicks: 0, RetryCount: 0, RetryDelayMs: 0));
                }
                break;

            case "D_leancorpus_wrapper":
                operationStartedAt = Stopwatch.GetTimestamp();
                observation.WriteToSyncGapMs = Milliseconds(observation.TimestampAfterLastClose, operationStartedAt);
                for (int index = 0; index < files.Count; index++)
                {
                    PayloadFile file = files[index];
                    wrapperObserver.Reset();
                    long startedAt = Stopwatch.GetTimestamp();
                    bool succeeded = true;
                    int error = 0;
                    using (DurabilitySpikeInstrumentation.Begin(wrapperObserver))
                    {
                        try { DirectoryFsync.SyncFile(file.Path, strict: true); }
                        catch (Exception ex)
                        {
                            succeeded = false;
                            error = ex.HResult & 0xffff;
                        }
                    }
                    long endedAt = Stopwatch.GetTimestamp();
                    observation.OperationSuccess &= succeeded;
                    rows.Add(new MechanismFileRow(
                        observation, file.Index, file.Length,
                        0, 0, 0, 0, true, succeeded, true, error,
                        FlushAttempted: true,
                        WrapperCallTicks: endedAt - startedAt,
                        RetryCount: wrapperObserver.RetryCount,
                        RetryDelayMs: wrapperObserver.RetryDelayMs));
                }
                observation.TotalMs = Milliseconds(operationStartedAt, Stopwatch.GetTimestamp());
                break;

            default:
                throw new InvalidDataException($"Unsupported primary variant '{observation.Variant}'.");
        }
    }

    private static void RunOpenOne(
        PayloadFile file,
        bool flush,
        MechanismObservation observation,
        List<MechanismFileRow> rows)
    {
        long fullStartedAt = Stopwatch.GetTimestamp();
        SafeFileHandle? handle = TryOpen(file.Path, out long openTicks, out int error);
        bool flushSuccess = !flush;
        long flushTicks = 0;
        if (handle is not null && flush)
        {
            long flushStartedAt = Stopwatch.GetTimestamp();
            flushSuccess = NativeFileOperations.Flush(handle, out error);
            flushTicks = Stopwatch.GetTimestamp() - flushStartedAt;
        }
        long closeStartedAt = Stopwatch.GetTimestamp();
        bool closeSuccess = handle is not null;
        if (handle is not null)
        {
            try { NativeFileOperations.Close(handle); }
            catch { closeSuccess = false; }
        }
        long closeTicks = Stopwatch.GetTimestamp() - closeStartedAt;
        long fullTicks = Stopwatch.GetTimestamp() - fullStartedAt;
        observation.OperationSuccess &= handle is not null && flushSuccess && closeSuccess;
        rows.Add(new MechanismFileRow(
            observation, file.Index, file.Length, openTicks, flushTicks, closeTicks,
            fullTicks, handle is not null, flushSuccess, closeSuccess, error,
            FlushAttempted: flush, WrapperCallTicks: 0, RetryCount: 0, RetryDelayMs: 0));
    }

    private static SafeFileHandle? TryOpen(string path, out long openTicks, out int error)
    {
        long startedAt = Stopwatch.GetTimestamp();
        SafeFileHandle handle = NativeFileOperations.Open(path, out error);
        long endedAt = Stopwatch.GetTimestamp();
        openTicks = endedAt - startedAt;
        return handle.IsInvalid ? DisposeInvalid(handle) : handle;
    }

    private static SafeFileHandle? DisposeInvalid(SafeFileHandle handle)
    {
        handle.Dispose();
        return null;
    }

    private static void RunCleanReflush(
        IReadOnlyList<PayloadFile> files,
        MechanismObservation observation,
        List<CleanReflushRow> rows)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("E_clean_reflush_preopened is Windows-only.");
        var handles = new List<(PayloadFile File, SafeFileHandle? Handle, int Error)>(files.Count);
        observation.OperationSuccess = true;
        observation.WriteToSyncGapMs = Milliseconds(
            observation.TimestampAfterLastClose, Stopwatch.GetTimestamp());
        foreach (PayloadFile file in files)
        {
            SafeFileHandle handle = NativeFileOperations.Open(file.Path, out int error);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                handles.Add((file, null, error));
                observation.OperationSuccess = false;
            }
            else
            {
                handles.Add((file, handle, 0));
            }
        }
        long preflushStartedAt = Stopwatch.GetTimestamp();
        foreach ((PayloadFile _, SafeFileHandle? handle, _) in handles)
        {
            if (handle is not null && !NativeFileOperations.Flush(handle, out _))
                observation.OperationSuccess = false;
        }
        long cleanStartedAt = Stopwatch.GetTimestamp();
        observation.PreflushMs = Milliseconds(preflushStartedAt, cleanStartedAt);
        foreach ((PayloadFile file, SafeFileHandle? handle, int openError) in handles)
        {
            long flushStartedAt = Stopwatch.GetTimestamp();
            int error = 0;
            bool succeeded = handle is not null && NativeFileOperations.Flush(handle, out error);
            int flushError = handle is null ? openError : succeeded ? 0 : error;
            long flushEndedAt = Stopwatch.GetTimestamp();
            observation.OperationSuccess &= succeeded;
            rows.Add(new CleanReflushRow(
                observation, file.Index, file.Length,
                flushEndedAt - flushStartedAt, succeeded, flushError));
        }
        long cleanEndedAt = Stopwatch.GetTimestamp();
        observation.CleanReflushMs = Milliseconds(cleanStartedAt, cleanEndedAt);
        observation.TotalMs = observation.CleanReflushMs;
        observation.CleanReflushPerFileMs = observation.CleanReflushMs / files.Count;
        foreach ((_, SafeFileHandle? handle, _) in handles)
            handle?.Dispose();
    }

    private static List<PayloadFile> WritePayloadFiles(
        string directory,
        long payloadBytes,
        int fileCount,
        out long timestampAfterLastClose,
        out string payloadSha256)
    {
        long baseLength = payloadBytes / fileCount;
        long remainder = payloadBytes % fileCount;
        long globalOffset = 0;
        timestampAfterLastClose = 0;
        var files = new List<PayloadFile>(fileCount);
        byte[] buffer = new byte[1024 * 1024];
        using var payloadHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int fileIndex = 0; fileIndex < fileCount; fileIndex++)
        {
            long length = baseLength + (fileIndex == fileCount - 1 ? remainder : 0);
            string path = Path.Combine(directory, $"payload-{fileIndex:D4}.bin");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            string expectedSha256;
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                       bufferSize: buffer.Length, FileOptions.SequentialScan))
            {
                long remaining = length;
                long fileOffset = globalOffset;
                while (remaining > 0)
                {
                    int count = (int)Math.Min(buffer.Length, remaining);
                    for (int index = 0; index < count; index++)
                        buffer[index] = unchecked((byte)(((fileOffset + index) * 31 + 17) & 0xff));
                    output.Write(buffer, 0, count);
                    hash.AppendData(buffer, 0, count);
                    payloadHash.AppendData(buffer, 0, count);
                    fileOffset += count;
                    remaining -= count;
                }
                expectedSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            timestampAfterLastClose = Stopwatch.GetTimestamp();
            files.Add(new PayloadFile(fileIndex, path, length, expectedSha256));
            globalOffset += length;
        }
        payloadSha256 = Convert.ToHexString(payloadHash.GetHashAndReset()).ToLowerInvariant();
        return files;
    }

    private static bool ValidateFiles(IReadOnlyList<PayloadFile> files)
    {
        byte[] buffer = new byte[1024 * 1024];
        foreach (PayloadFile file in files)
        {
            using var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: buffer.Length, FileOptions.SequentialScan);
            if (input.Length != file.ExpectedLength)
                return false;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer, 0, read);
            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), file.ExpectedSha256,
                    StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static List<MechanismOrderRow> ReadOrder(string path)
    {
        var rows = new List<MechanismOrderRow>();
        using var reader = new StreamReader(path);
        _ = reader.ReadLine();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string[] fields = line.Split(',');
            if (fields.Length != 10)
                throw new InvalidDataException($"Invalid mechanism order row: {line}");
            rows.Add(new MechanismOrderRow(fields[0], int.Parse(fields[1]), fields[2], uint.Parse(fields[3]),
                long.Parse(fields[5]), int.Parse(fields[6]), fields[7], int.Parse(fields[8]),
                bool.Parse(fields[9]), int.Parse(fields[4])));
        }
        return rows;
    }

    private static void WriteMechanismCsv(string path, IReadOnlyList<MechanismObservation> observations)
    {
        using var csv = new CsvFile(path,
            "environment", "launch_index", "launch_seed", "shuffle_seed", "order_index",
            "payload_bytes", "file_count", "variant", "observation_index", "warm_up",
            "success", "total_ms", "write_to_sync_gap_ms", "preflush_ms", "clean_reflush_ms",
            "clean_reflush_per_file_ms", "file_sizes", "expected_sha256", "hash_validation",
            "payload_sha256", "file_sizes_validated", "error", "trial_directory");
        foreach (MechanismObservation value in observations)
            csv.WriteRow(value.Environment, value.LaunchIndex, value.LaunchSeed, value.ShuffleSeed,
                value.OrderIndex, value.PayloadBytes, value.FileCount, value.Variant,
                value.ObservationIndex, value.IsWarmUp, value.Success, value.TotalMs,
                value.WriteToSyncGapMs, value.PreflushMs, value.CleanReflushMs,
                value.CleanReflushPerFileMs, value.FileSizes, value.ExpectedHashes,
                value.HashValidation, value.PayloadSha256, value.FileSizesValidated,
                value.Error, value.TrialDirectory);
    }

    private static void WriteMechanismFileCsv(string path, IReadOnlyList<MechanismFileRow> rows)
    {
        using var csv = new CsvFile(path,
            "environment", "launch_index", "payload_bytes", "file_count", "variant",
            "observation_index", "warm_up", "file_index", "file_bytes", "open_ms",
            "flush_ms", "close_ms", "full_ms", "open_success", "flush_attempted",
            "flush_success", "close_success", "win32_error", "wrapper_call_ms",
            "retry_count", "retry_delay_ms");
        foreach (MechanismFileRow row in rows)
            csv.WriteRow(row.Observation.Environment, row.Observation.LaunchIndex,
                row.Observation.PayloadBytes, row.Observation.FileCount, row.Observation.Variant,
                row.Observation.ObservationIndex, row.Observation.IsWarmUp, row.FileIndex,
                row.FileBytes, TicksToMs(row.OpenTicks), TicksToMs(row.FlushTicks),
                TicksToMs(row.CloseTicks), TicksToMs(row.FullTicks), row.OpenSuccess,
                row.FlushAttempted, row.FlushSuccess, row.CloseSuccess, row.Win32Error,
                TicksToMs(row.WrapperCallTicks), row.RetryCount, row.RetryDelayMs);
    }

    private static void WriteCleanCsv(string path, IReadOnlyList<CleanReflushRow> rows)
    {
        using var csv = new CsvFile(path,
            "environment", "launch_index", "launch_seed", "payload_bytes", "file_count",
            "observation_index", "warm_up", "file_index", "file_bytes", "preflush_ms",
            "clean_reflush_ms", "clean_reflush_per_file_ms", "file_clean_reflush_ms",
            "flush_success", "win32_error");
        foreach (CleanReflushRow row in rows)
            csv.WriteRow(row.Observation.Environment, row.Observation.LaunchIndex,
                row.Observation.LaunchSeed, row.Observation.PayloadBytes, row.Observation.FileCount,
                row.Observation.ObservationIndex, row.Observation.IsWarmUp, row.FileIndex,
                row.FileBytes, row.Observation.PreflushMs, row.Observation.CleanReflushMs,
                row.Observation.CleanReflushPerFileMs, TicksToMs(row.CleanFlushTicks),
                row.FlushSuccess, row.Win32Error);
    }

    private static double Milliseconds(long startedAt, long completedAt)
        => TicksToMs(completedAt - startedAt);

    private static double TicksToMs(long ticks)
        => ticks * 1000d / Stopwatch.Frequency;

    private static void EnsureEvidenceOutsideDataRoot(string dataRoot, string evidence)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)) + Path.DirectorySeparatorChar;
        string result = Path.GetFullPath(evidence) + Path.DirectorySeparatorChar;
        if (result.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Evidence must be outside the measured data root.");
    }

    private sealed record PayloadFile(int Index, string Path, long Length, string ExpectedSha256)
    {
        internal long ExpectedLength => Length;
    }

    private sealed record MechanismOrderRow(
        string LaunchGroup,
        int LaunchIndex,
        string LaunchSeed,
        uint ShuffleSeed,
        long PayloadBytes,
        int FileCount,
        string Variant,
        int ObservationIndex,
        bool IsWarmUp,
        int OrderIndex);

    private sealed class MechanismObservation
    {
        internal string Environment { get; init; } = string.Empty;
        internal int LaunchIndex { get; init; }
        internal string LaunchSeed { get; init; } = string.Empty;
        internal uint ShuffleSeed { get; init; }
        internal long PayloadBytes { get; init; }
        internal int FileCount { get; init; }
        internal string Variant { get; init; } = string.Empty;
        internal int ObservationIndex { get; init; }
        internal bool IsWarmUp { get; init; }
        internal int OrderIndex { get; init; }
        internal string TrialDirectory { get; init; } = string.Empty;
        internal bool Success { get; set; }
        internal bool OperationSuccess { get; set; }
        internal bool HashValidation { get; set; }
        internal bool FileSizesValidated { get; set; }
        internal string? Error { get; set; }
        internal double TotalMs { get; set; }
        internal double WriteToSyncGapMs { get; set; }
        internal double PreflushMs { get; set; }
        internal double CleanReflushMs { get; set; }
        internal double CleanReflushPerFileMs { get; set; }
        internal string PayloadSha256 { get; set; } = string.Empty;
        internal string FileSizes { get; set; } = string.Empty;
        internal string ExpectedHashes { get; set; } = string.Empty;
        internal long TimestampAfterLastClose { get; set; }
    }

    private sealed record MechanismFileRow(
        MechanismObservation Observation,
        int FileIndex,
        long FileBytes,
        long OpenTicks,
        long FlushTicks,
        long CloseTicks,
        long FullTicks,
        bool OpenSuccess,
        bool FlushSuccess,
        bool CloseSuccess,
        int Win32Error,
        bool FlushAttempted,
        long WrapperCallTicks,
        int RetryCount,
        double RetryDelayMs);

    private sealed record CleanReflushRow(
        MechanismObservation Observation,
        int FileIndex,
        long FileBytes,
        long CleanFlushTicks,
        bool FlushSuccess,
        int Win32Error);

    private sealed class MechanismObserver(bool captureEvents = false) : IDurabilitySpikeObserver
    {
        private readonly bool _captureEvents = captureEvents;
        private readonly List<DurabilitySpikeEvent> _events = [];
        private readonly List<DurabilitySpikeOperation> _nativeCalls = [];
        internal int RetryCount { get; private set; }
        internal double RetryDelayMs { get; private set; }
        internal IReadOnlyList<DurabilitySpikeEvent> Events => _events;
        internal IReadOnlyList<DurabilitySpikeOperation> NativeCalls => _nativeCalls;

        public bool SyncDirectoryAfterMarkerPublication => true;
        public void OnEvent(in DurabilitySpikeEvent value)
        {
            if (_captureEvents)
                _events.Add(value);
            if (value.Operation == DurabilitySpikeOperation.RetryDelay)
            {
                RetryCount++;
                RetryDelayMs += value.Value;
            }
        }
        public void OnCheckpoint(DurabilitySpikeCheckpoint checkpoint, string? path) { }
        public void BeforeDurabilityOperation(string operationId) { }
        public void AfterDurabilityOperation(string operationId) { }
        public DirtyFileTracker.DirtyFile PublishMarker(string temporaryPath, string destinationPath, bool overwrite)
            => throw new InvalidOperationException("A mechanism observation cannot publish an index marker.");
        internal void RecordNativeCall(DurabilitySpikeOperation operation) => _nativeCalls.Add(operation);
        internal void Reset()
        {
            RetryCount = 0;
            RetryDelayMs = 0;
        }
    }

    private static string NativeCallName(DurabilitySpikeOperation operation) => operation switch
    {
        DurabilitySpikeOperation.WindowsOpen => "CreateFileW",
        DurabilitySpikeOperation.WindowsFlush => "FlushFileBuffers",
        DurabilitySpikeOperation.WindowsClose => "SafeFileHandle close",
        _ => operation.ToString()
    };
}

internal static class NativeFileOperations
{
    private const uint GenericWrite = 0x40000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint ShareDelete = 0x00000004;
    private const uint OpenExisting = 3;

    internal static SafeFileHandle Open(string path, out int error)
    {
        var observer = DurabilitySpikeInstrumentation.Current;
        long startedAt = observer is null ? 0 : Stopwatch.GetTimestamp();
        DurabilitySpikeInstrumentation.RecordNativeCall(DurabilitySpikeOperation.WindowsOpen);
        SafeFileHandle handle = CreateFileW(path, GenericWrite, ShareRead | ShareWrite | ShareDelete,
            0, OpenExisting, 0, 0);
        error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        if (observer is not null)
            observer.OnEvent(new DurabilitySpikeEvent(DurabilitySpikeOperation.WindowsOpen,
                path, startedAt, Stopwatch.GetTimestamp(), error, !handle.IsInvalid));
        return handle;
    }

    internal static bool Flush(SafeFileHandle handle, out int error)
    {
        var observer = DurabilitySpikeInstrumentation.Current;
        long startedAt = observer is null ? 0 : Stopwatch.GetTimestamp();
        DurabilitySpikeInstrumentation.RecordNativeCall(DurabilitySpikeOperation.WindowsFlush);
        bool success = FlushFileBuffers(handle);
        error = success ? 0 : Marshal.GetLastPInvokeError();
        if (observer is not null)
            observer.OnEvent(new DurabilitySpikeEvent(DurabilitySpikeOperation.WindowsFlush,
                null, startedAt, Stopwatch.GetTimestamp(), error, success));
        return success;
    }

    internal static void Close(SafeFileHandle handle)
    {
        var observer = DurabilitySpikeInstrumentation.Current;
        long startedAt = observer is null ? 0 : Stopwatch.GetTimestamp();
        DurabilitySpikeInstrumentation.RecordNativeCall(DurabilitySpikeOperation.WindowsClose);
        bool succeeded = false;
        try
        {
            handle.Dispose();
            succeeded = true;
        }
        finally
        {
            if (observer is not null)
                observer.OnEvent(new DurabilitySpikeEvent(DurabilitySpikeOperation.WindowsClose,
                    null, startedAt, Stopwatch.GetTimestamp(), succeeded: succeeded));
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle handle);
}

internal static class SeededShuffle
{
    internal static void Shuffle<T>(IList<T> values, uint seed)
    {
        ulong state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        for (int index = values.Count - 1; index > 0; index--)
        {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            ulong random = state * 0x2545F4914F6CDD1DUL;
            int other = (int)(random % (uint)(index + 1));
            (values[index], values[other]) = (values[other], values[index]);
        }
    }
}
