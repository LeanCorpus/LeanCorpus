using System.Text;

using Rowles.LeanCorpus.Diagnostics;

namespace Rowles.LeanCorpus.Store;

/// <summary>
/// Writes a file through a same-directory temporary file and atomic replacement.
/// </summary>
internal static class IndexAtomicFileWriter
{
    public static void WriteText(string path, string contents, bool durable)
        => WriteText(path, contents, durable, syncDirectory: true);

    internal static void WriteText(string path, string contents, bool durable, bool syncDirectory)
    {
        Write(path, durable, syncDirectory, stream =>
        {
            using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(contents);
            writer.Flush();
        });
    }

    public static void Write(string path, bool durable, Action<Stream> write)
        => Write(path, durable, syncDirectory: true, write);

    internal static void Write(string path, bool durable, bool syncDirectory, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);
        bool commitMarker = Path.GetFileName(path).StartsWith("segments_", StringComparison.Ordinal);
        if (durable)
            Diagnostics.FileSystemDiagnostics.RecordImmediateDurableAtomicWrite();

        // A unique same-directory name preserves atomic rename semantics while
        // allowing concurrent writers to different generations of the same file.
        var tempPath = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            using (var stream = FileOpenRetry.CreateTrackedIndexFile(tempPath))
            {
                write(stream);
                if (durable)
                {
                    FileOpenRetry.FlushToDisk(stream);
                    if (commitMarker)
                    {
                        SpikeInstrumentation.Record(SpikeInstrumentationPoint.CommitMarkerTempPersisted, tempPath);
                        SpikeInstrumentation.Checkpoint("after_commit_marker_tmp_persisted");
                    }
                }
            }
            var publishedFile = FileOpenRetry.Move(tempPath, path, overwrite: true);
            SpikeInstrumentation.Record(SpikeInstrumentationPoint.AtomicReplace, path);
            if (commitMarker)
            {
                SpikeInstrumentation.Record(SpikeInstrumentationPoint.CommitMarkerRenamed, path);
                SpikeInstrumentation.Checkpoint("after_commit_marker_renamed");
            }

            if (durable && syncDirectory)
            {
                try
                {
                    DirectoryFsync.Sync(Path.GetDirectoryName(path) ?? string.Empty, strict: true);
                }
                finally
                {
                    if (commitMarker)
                    {
                        SpikeInstrumentation.Record(SpikeInstrumentationPoint.CommitDirectoryPersistReturned, path);
                        SpikeInstrumentation.Checkpoint("after_directory_persist_attempt");
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
