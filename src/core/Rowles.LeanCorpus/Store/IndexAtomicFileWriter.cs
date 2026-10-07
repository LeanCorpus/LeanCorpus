using System.Text;

namespace Rowles.LeanCorpus.Store;

/// <summary>
/// Writes a file through a same-directory temporary file and atomic replacement.
/// </summary>
internal static class IndexAtomicFileWriter
{
    public static void WriteText(string path, string contents, bool durable)
        => WriteText(path, contents, durable, syncDirectory: true);

    internal static void WriteText(string path, string contents, bool durable, bool syncDirectory, bool isCommitMarker = false)
    {
        Write(path, durable, syncDirectory, stream =>
        {
            using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(contents);
            writer.Flush();
        }, isCommitMarker);
    }

    public static void Write(string path, bool durable, Action<Stream> write)
        => Write(path, durable, syncDirectory: true, write);

    internal static void Write(string path, bool durable, bool syncDirectory, Action<Stream> write, bool isCommitMarker = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);
        if (durable)
            Diagnostics.FileSystemDiagnostics.RecordImmediateDurableAtomicWrite();

        // A unique same-directory name preserves atomic rename semantics while
        // allowing concurrent writers to different generations of the same file.
        var tempPath = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        IDurabilitySpikeObserver? observer = isCommitMarker
            ? DurabilitySpikeInstrumentation.Current
            : null;
        try
        {
            using (var stream = FileOpenRetry.CreateTrackedIndexFile(tempPath))
            {
                long writeStartedAt = observer is null
                    ? 0
                    : System.Diagnostics.Stopwatch.GetTimestamp();
                write(stream);
                if (observer is not null)
                {
                    DurabilitySpikeInstrumentation.Record(new DurabilitySpikeEvent(
                        DurabilitySpikeOperation.MarkerWrite,
                        tempPath,
                        writeStartedAt,
                        System.Diagnostics.Stopwatch.GetTimestamp()));
                    DurabilitySpikeInstrumentation.Checkpoint(
                        DurabilitySpikeCheckpoint.AfterCommitMarkerTempWritten,
                        tempPath);
                }

                if (durable)
                {
                    long flushStartedAt = observer is null
                        ? 0
                        : System.Diagnostics.Stopwatch.GetTimestamp();
                    bool flushSucceeded = false;
                    try
                    {
                        if (observer is not null)
                            DurabilitySpikeInstrumentation.BeforeDurabilityOperation("temp_marker_file_persist");
                        FileOpenRetry.FlushToDisk(stream);
                        flushSucceeded = true;
                        if (observer is not null)
                            DurabilitySpikeInstrumentation.AfterDurabilityOperation("temp_marker_file_persist");
                    }
                    finally
                    {
                        if (observer is not null)
                        {
                            long flushCompletedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                            DurabilitySpikeInstrumentation.Record(new DurabilitySpikeEvent(
                                DurabilitySpikeOperation.FilePersist,
                                tempPath,
                                flushStartedAt,
                                flushCompletedAt,
                                succeeded: flushSucceeded));
                            DurabilitySpikeInstrumentation.Record(new DurabilitySpikeEvent(
                                DurabilitySpikeOperation.MarkerFlush,
                                tempPath,
                                flushStartedAt,
                                flushCompletedAt,
                                succeeded: flushSucceeded));
                        }
                    }
                }
            }
            if (observer is not null)
            {
                DurabilitySpikeInstrumentation.Checkpoint(
                    DurabilitySpikeCheckpoint.AfterCommitMarkerTempPersisted,
                    tempPath);
                DurabilitySpikeInstrumentation.Checkpoint(
                    DurabilitySpikeCheckpoint.BeforePublicationCall,
                    path);
                DurabilitySpikeInstrumentation.BeforeDurabilityOperation("publication_call");
            }
            long publicationStartedAt = observer is null
                ? 0
                : System.Diagnostics.Stopwatch.GetTimestamp();
            var publishedFile = observer is null
                ? FileOpenRetry.Move(tempPath, path, overwrite: true)
                : DurabilitySpikeInstrumentation.PublishMarker(tempPath, path, overwrite: true);
            if (observer is not null)
            {
                DurabilitySpikeInstrumentation.Record(new DurabilitySpikeEvent(
                    DurabilitySpikeOperation.MarkerPublication,
                    path,
                    publicationStartedAt,
                    System.Diagnostics.Stopwatch.GetTimestamp()));
                DurabilitySpikeInstrumentation.Checkpoint(
                    DurabilitySpikeCheckpoint.AfterPublicationCall,
                    path);
            }

            bool syncPublishedDirectory = syncDirectory &&
                (observer?.SyncDirectoryAfterMarkerPublication ?? true);
            if (durable && syncPublishedDirectory)
            {
                string directoryPath = Path.GetDirectoryName(path) ?? string.Empty;
                long directorySyncStartedAt = observer is null
                    ? 0
                    : System.Diagnostics.Stopwatch.GetTimestamp();
                bool directorySyncSucceeded = false;
                try
                {
                    if (observer is not null)
                        DurabilitySpikeInstrumentation.BeforeDurabilityOperation("directory_persist_postpublication");
                    DirectoryFsync.Sync(directoryPath, strict: true);
                    directorySyncSucceeded = true;
                    if (observer is not null)
                        DurabilitySpikeInstrumentation.AfterDurabilityOperation("directory_persist_postpublication");
                }
                finally
                {
                    if (observer is not null)
                    {
                        DurabilitySpikeInstrumentation.Record(new DurabilitySpikeEvent(
                            DurabilitySpikeOperation.PostPublicationDirectorySync,
                            directoryPath,
                            directorySyncStartedAt,
                            System.Diagnostics.Stopwatch.GetTimestamp(),
                            succeeded: directorySyncSucceeded));
                        DurabilitySpikeInstrumentation.Checkpoint(
                            DurabilitySpikeCheckpoint.AfterDirectoryPersistAttempt,
                            directoryPath);
                    }
                }
            }

            if (durable)
                DirtyFileTracker.MarkSynced(publishedFile);
        }
        catch
        {
            try { FileOpenRetry.Delete(tempPath); } catch (Exception ex) { Diagnostics.LeanCorpusActivitySource.TraceSwallowed(ex, "atomic-write temp file cleanup"); }
            throw;
        }
    }
}
