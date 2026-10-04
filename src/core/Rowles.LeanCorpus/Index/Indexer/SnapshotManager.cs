using Rowles.LeanCorpus.Index.Backup;
using Rowles.LeanCorpus.Store;

namespace Rowles.LeanCorpus.Index.Indexer;

/// <summary>
/// Manages index snapshots, NRT segment access, and backup operations.
/// All methods are static — operates via a single <see cref="IndexWriter"/> parameter.
/// </summary>
internal static class SnapshotManager
{
    public static HashSet<string> GetSnapshotProtectedSegments(IndexWriter writer)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snap in writer.HeldSnapshots)
        {
            foreach (var seg in snap.Segments)
                ids.Add(seg.SegmentId);
        }
        return ids;
    }

    public static IReadOnlyList<SegmentInfo> GetNrtSegments(IndexWriter writer)
    {
        lock (writer.WriteLock)
        {
            DwptManager.FlushDwptPool(writer);
            DwptManager.WaitForPendingFlushes(writer);
            return writer.CommittedSegments.Select(static segment => segment.DeepCopy()).ToList().AsReadOnly();
        }
    }

    public static IndexSnapshot CreateSnapshot(IndexWriter writer)
    {
        lock (writer.WriteLock)
        {
            DwptManager.FlushDwptPool(writer);
            DwptManager.WaitForPendingFlushes(writer);

            var snapshot = new IndexSnapshot(
                writer.CommitGeneration,
                writer.CommittedSegments);

            writer.HeldSnapshots.Add(snapshot);
            return snapshot;
        }
    }

    public static void ReleaseSnapshot(IndexWriter writer, IndexSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (writer.WriteLock)
        {
            writer.HeldSnapshots.Remove(snapshot);
            CommitManager.PruneDeletionGenerations(writer);
        }
    }

    public static IndexBackupManifest CreateBackupManifest(
        IndexSnapshot snapshot,
        string directoryPath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        EnsureMatchesPublishedCommit(snapshot, directoryPath);
        return IndexBackup.CreateManifest(
            directoryPath,
            new IndexBackupOptions { CommitGeneration = snapshot.CommitGeneration });
    }

    public static IndexBackupResult BackupSnapshot(
        IndexSnapshot snapshot,
        string backupDirectoryPath,
        string directoryPath,
        IndexBackupOptions? options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        EnsureMatchesPublishedCommit(snapshot, directoryPath);
        var effectiveOptions = new IndexBackupOptions
        {
            CommitGeneration = snapshot.CommitGeneration,
            OverwriteBackupDirectory = options?.OverwriteBackupDirectory ?? false,
            IncludeCommitStats = options?.IncludeCommitStats ?? true,
            PreviousBackupDirectoryPath = options?.PreviousBackupDirectoryPath
        };
        return IndexBackup.Backup(directoryPath, backupDirectoryPath, effectiveOptions, cancellationToken);
    }
    private static void EnsureMatchesPublishedCommit(IndexSnapshot snapshot, string directoryPath)
    {
        var commitPath = Path.Combine(directoryPath, $"segments_{snapshot.CommitGeneration}");
        var check = new IndexCheckResult();
        var commit = FileOpenRetry.FileExists(commitPath)
            ? IndexFileInspector.TryReadCommit(commitPath, snapshot.CommitGeneration, check)
            : null;
        if (commit is null || commit.Segments.Count != snapshot.Segments.Count)
            throw new InvalidOperationException("Snapshot backup requires an exact published commit. Commit pending changes and capture a new snapshot first.");

        for (int i = 0; i < commit.Segments.Count; i++)
        {
            var expected = SegmentInfo.ReadFrom(Path.Combine(directoryPath, commit.Segments[i] + ".seg"));
            commit.GetSegmentState(i)?.ApplyTo(expected);
            var actual = snapshot.Segments[i];
            if (!string.Equals(expected.SegmentId, actual.SegmentId, StringComparison.Ordinal)
                || expected.DocCount != actual.DocCount
                || expected.LiveDocCount != actual.LiveDocCount
                || expected.DelGeneration != actual.DelGeneration
                || expected.EarliestSoftDeleteTimestamp != actual.EarliestSoftDeleteTimestamp)
            {
                throw new InvalidOperationException("Snapshot contains uncommitted segment state. Commit pending changes and capture a new snapshot before backup.");
            }
        }
    }

}
