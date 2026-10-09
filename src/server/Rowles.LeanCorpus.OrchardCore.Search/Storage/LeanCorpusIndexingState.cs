using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rowles.LeanCorpus.OrchardCore.Search.Storage;

internal sealed record LeanCorpusIndexingState
{
    public int Version { get; init; } = 1;
    public long LastTaskId { get; init; }
}

internal enum LeanCorpusFailurePoint
{
    BeforeDocumentWrite,
    AfterDocumentWriteBeforeCommit,
    AfterCommitBeforeCursor,
    DuringCursorWrite,
    AfterCursorFlushBeforePublish,
    AfterCursorPublish,
    AfterDeleteCommitBeforeCursor,
    AfterSchemaPublishBeforeCommit,
}

internal interface ILeanCorpusFailureInjector
{
    void Check(LeanCorpusFailurePoint point);
}

internal sealed class NoOpLeanCorpusFailureInjector : ILeanCorpusFailureInjector
{
    public void Check(LeanCorpusFailurePoint point) { }
}

internal static class LeanCorpusIndexingStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static async Task<long> ReadLastTaskIdAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return 0;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        LeanCorpusIndexingState state;
        try
        {
            state = await JsonSerializer.DeserializeAsync<LeanCorpusIndexingState>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("The LeanCorpus cursor file is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The LeanCorpus cursor file contains invalid JSON.", exception);
        }

        if (state.Version != 1 || state.LastTaskId < 0)
            throw new InvalidDataException("The LeanCorpus cursor file has an unsupported version or invalid task ID.");

        return state.LastTaskId;
    }

    public static async Task PublishAsync(
        string path,
        long taskId,
        ILeanCorpusFailureInjector failures,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(taskId);
        ArgumentNullException.ThrowIfNull(failures);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            byte[] stateBytes = JsonSerializer.SerializeToUtf8Bytes(
                new LeanCorpusIndexingState { LastTaskId = taskId }, SerializerOptions);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                int firstChunkLength = Math.Max(1, stateBytes.Length / 2);
                await stream.WriteAsync(stateBytes.AsMemory(0, firstChunkLength), cancellationToken).ConfigureAwait(false);
                failures.Check(LeanCorpusFailurePoint.DuringCursorWrite);
                await stream.WriteAsync(stateBytes.AsMemory(firstChunkLength), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            failures.Check(LeanCorpusFailurePoint.AfterCursorFlushBeforePublish);
            File.Move(temporaryPath, path, overwrite: true);
            failures.Check(LeanCorpusFailurePoint.AfterCursorPublish);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
