using System.Text.Json;
using System.Text.Json.Serialization;
using Rowles.LeanCorpus.OrchardCore.Search.Storage;

namespace Rowles.LeanCorpus.OrchardCore.Search.Indexing;

internal sealed class LeanCorpusSchemaStore(ILeanCorpusFailureInjector failures)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async Task<LeanCorpusSchemaManifest> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            throw new InvalidDataException("The LeanCorpus schema manifest is missing.");

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var manifest = await JsonSerializer.DeserializeAsync<LeanCorpusSchemaManifest>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("The LeanCorpus schema manifest is empty.");
            if (manifest.Version != 1 || manifest.Fields is null)
                throw new InvalidDataException("The LeanCorpus schema manifest has an unsupported version.");
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The LeanCorpus schema manifest contains invalid JSON.", exception);
        }
    }

    public async Task PublishAsync(string path, LeanCorpusSchemaManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
            failures.Check(LeanCorpusFailurePoint.AfterSchemaPublishBeforeCommit);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
