using System.Diagnostics;
using System.Text;
using Rowles.LeanCorpus.Diagnostics;

namespace Rowles.LeanCorpus.CompoundDurabilitySpike;

internal sealed class SpikeObserver(string? failpoint = null, string? controlLogPath = null)
{
    private readonly string? _failpoint = failpoint;
    private readonly string? _controlLogPath = controlLogPath;
    private bool _insideCommit;
    private long _packStartedAt;
    private double _packMilliseconds;
    private bool _packObserved;
    private bool _packEmbeddedInCommit = true;
    private long _metadataStartedAt;
    private long _metadataCompletedAt;
    private long _durabilityStartedAt;
    private long _durabilityCompletedAt;

    public long PackMemberCount { get; private set; }
    public long PackInputBytes { get; private set; }
    public long PackOutputBytes { get; private set; }
    public long PackSourceReadBytes { get; private set; }
    public long PackTempWrittenBytes { get; private set; }
    public long PackTempExplicitPersistRequests { get; private set; }
    public bool PackEmbeddedInCommit => _packObserved && _packEmbeddedInCommit;
    public long DurabilityCandidateFiles { get; private set; }
    public long DurabilityCandidateBytes { get; private set; }
    public long FilePersistRequests { get; private set; }
    public long FilePersistSuccess { get; private set; }
    public long FilePersistFailed { get; private set; }
    public long DirectoryPersistRequests { get; private set; }
    public long DirectoryPersistSuccess { get; private set; }
    public long DirectoryPersistUnsupported { get; private set; }
    public long DirectoryPersistFailed { get; private set; }
    public long AtomicReplaceCount { get; private set; }
    public long CommitMarkerPersistRequests { get; private set; }
    public long FilesRenamed { get; private set; }
    public long FilesDeleted { get; private set; }
    public string DirectoryPersistOutcome { get; private set; } = string.Empty;

    public double PackMilliseconds => _packMilliseconds;
    public double MetadataPrepareMilliseconds => Elapsed(_metadataStartedAt, _metadataCompletedAt);
    public double DurabilityMilliseconds => Elapsed(_durabilityStartedAt, _durabilityCompletedAt);

    public long DurabilityEndTimestamp => _durabilityCompletedAt;

    public void Attach()
        => SpikeInstrumentation.Observer = Observe;

    public void BeginCommit()
    {
        _insideCommit = true;
    }

    public void Checkpoint(string name)
        => SpikeInstrumentation.Checkpoint(name);

    private void Observe(SpikeInstrumentationEvent item)
    {
        long now = Stopwatch.GetTimestamp();
        switch (item.Point)
        {
            case SpikeInstrumentationPoint.PackStarted:
                _packObserved = true;
                if (!_insideCommit)
                    _packEmbeddedInCommit = false;
                _packStartedAt = now;
                break;
            case SpikeInstrumentationPoint.PackMember:
                PackMemberCount++;
                break;
            case SpikeInstrumentationPoint.PackInputBytesCopied:
                PackInputBytes += item.Amount;
                break;
            case SpikeInstrumentationPoint.PackSourceBytesRead:
                PackSourceReadBytes += item.Amount;
                break;
            case SpikeInstrumentationPoint.PackTempClosed:
                PackTempWrittenBytes += item.Value;
                break;
            case SpikeInstrumentationPoint.PackPublished:
                PackOutputBytes += item.Value;
                break;
            case SpikeInstrumentationPoint.PackCompleted:
                if (_packStartedAt != 0)
                    _packMilliseconds += SpikeInfrastructure.Milliseconds(_packStartedAt, now);
                _packStartedAt = 0;
                break;
            case SpikeInstrumentationPoint.FileRenamed:
                FilesRenamed++;
                break;
            case SpikeInstrumentationPoint.FileDeleted:
                FilesDeleted++;
                break;
            case SpikeInstrumentationPoint.FilePersistRequested:
                FilePersistRequests++;
                if (IsCompoundTemporary(item.Path))
                    PackTempExplicitPersistRequests++;
                if (IsCommitMarkerTemporary(item.Path))
                    CommitMarkerPersistRequests++;
                if (_insideCommit && _durabilityStartedAt == 0)
                    _durabilityStartedAt = now;
                break;
            case SpikeInstrumentationPoint.FilePersistReturned:
                if (item.Value == 1)
                    FilePersistSuccess++;
                else
                    FilePersistFailed++;
                break;
            case SpikeInstrumentationPoint.DirectoryPersistRequested:
                DirectoryPersistRequests++;
                if (_insideCommit && _durabilityStartedAt == 0)
                    _durabilityStartedAt = now;
                break;
            case SpikeInstrumentationPoint.DirectoryPersistReturned:
                DirectoryPersistOutcome = item.Detail ?? string.Empty;
                if (item.Detail == "Succeeded")
                    DirectoryPersistSuccess++;
                else if (item.Detail is "Unsupported" or "SkippedUnsupported")
                    DirectoryPersistUnsupported++;
                else
                    DirectoryPersistFailed++;
                break;
            case SpikeInstrumentationPoint.DurabilityCandidates:
                DurabilityCandidateFiles = item.Value;
                DurabilityCandidateBytes = item.Amount;
                break;
            case SpikeInstrumentationPoint.CommitMetadataStarting:
                _metadataStartedAt = now;
                break;
            case SpikeInstrumentationPoint.CommitMetadataPrepared:
                _metadataCompletedAt = now;
                break;
            case SpikeInstrumentationPoint.AtomicReplace:
                AtomicReplaceCount++;
                break;
            case SpikeInstrumentationPoint.CommitDirectoryPersistReturned:
                _durabilityCompletedAt = now;
                break;
            case SpikeInstrumentationPoint.Checkpoint:
                HandleCheckpoint(item.Path);
                break;
        }
    }

    private void HandleCheckpoint(string? name)
    {
        if (!string.Equals(_failpoint, name, StringComparison.Ordinal))
            return;

        if (string.IsNullOrWhiteSpace(_controlLogPath))
            throw new InvalidOperationException("A control-log path is required for a recovery failpoint.");

        Directory.CreateDirectory(Path.GetDirectoryName(_controlLogPath)!);
        using (var stream = new FileStream(_controlLogPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            string directoryOutcome = string.IsNullOrEmpty(DirectoryPersistOutcome)
                ? "not_attempted"
                : DirectoryPersistOutcome;
            byte[] bytes = Encoding.UTF8.GetBytes(
                $"checkpoint={name}\nrecorded_utc={SpikeInfrastructure.CurrentUtc()}\ndirectory_persist_outcome={directoryOutcome}\n");
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        Environment.FailFast($"Compound durability spike failpoint: {name}");
    }

    private static double Elapsed(long start, long end)
        => start == 0 || end == 0 || end < start ? 0 : SpikeInfrastructure.Milliseconds(start, end);

    private static bool IsCompoundTemporary(string? path)
        => Path.GetFileName(path ?? string.Empty).EndsWith(".cfs.tmp", StringComparison.OrdinalIgnoreCase);

    private static bool IsCommitMarkerTemporary(string? path)
    {
        string name = Path.GetFileName(path ?? string.Empty);
        return name.StartsWith("segments_", StringComparison.Ordinal)
            && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }
}
