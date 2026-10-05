using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rowles.DataForge;
using Rowles.DataForge.Workloads;

namespace Rowles.LeanCorpus.CompoundDurabilitySpike;

internal static class Dataset
{
    internal const int RecordCount = 100_000;
    internal const int BaselineCount = 90_000;
    internal const int BatchStart = 90_000;
    internal const int BatchCount = 10_000;
    internal const int PayloadBytes = 67_108_864;

    private static readonly JsonSerializerOptions RecordJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static DataForgeDatasetIdentity Generate(SpikePaths paths)
    {
        if (File.Exists(paths.RecordsPath) || File.Exists(paths.IdentityPath))
            throw new IOException("The dataset is already materialised. Refusing to replace the frozen spike dataset.");

        var profile = new LeanCorpusSearchProfile();
        var generation = new DataForgeGenerationOptions(42, RecordCount);
        DataForgeMaterialisationSummary summary;
        using (var stream = new FileStream(paths.RecordsPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                   64 * 1024, FileOptions.SequentialScan))
        {
            summary = DataForgeMaterialiser.GenerateToStream(profile, generation, stream);
            stream.Flush(flushToDisk: true);
        }

        var identity = new DataForgeDatasetIdentity(
            DataForgeSourceKind.Generated,
            DataForgeVersions.DataForgeVersion,
            profile.Descriptor.ProfileId,
            profile.Descriptor.ProfileVersion,
            DatasetId: null,
            DatasetVersion: null,
            Seed: generation.Seed,
            RecordCount: summary.RecordCount,
            Parameters: generation.Parameters,
            Dependencies: profile.Dependencies.ToArray(),
            ContentSha256: summary.ContentSha256);

        WriteIdentity(paths.IdentityPath, identity, generation);
        BuildRecordOffsets(paths.RecordsPath, paths.OffsetsPath, RecordCount);
        return identity;
    }

    public static DataForgeDatasetIdentity ImportFrozen(SpikePaths target, string sourceRoot)
    {
        var source = new SpikePaths(sourceRoot);
        if (!File.Exists(source.IdentityPath) || !File.Exists(source.RecordsPath))
            throw new FileNotFoundException("The frozen DataForge dataset source must contain dataset-identity.json and dataset/records.ndjson.");
        if (File.Exists(target.IdentityPath) || File.Exists(target.RecordsPath) || File.Exists(target.OffsetsPath))
            throw new IOException("The destination already contains dataset files. Refusing to replace them.");

        Directory.CreateDirectory(target.DatasetDirectory);
        File.Copy(source.IdentityPath, target.IdentityPath);
        File.Copy(source.RecordsPath, target.RecordsPath);
        BuildRecordOffsets(target.RecordsPath, target.OffsetsPath, RecordCount);

        DataForgeDatasetIdentity identity = ReadIdentity(target);
        var profile = new LeanCorpusSearchProfile();
        var expectedGeneration = new DataForgeGenerationOptions(42, RecordCount);
        if (identity.DataForgeVersion != DataForgeVersions.DataForgeVersion
            || identity.ProfileId != profile.Descriptor.ProfileId
            || identity.ProfileVersion != profile.Descriptor.ProfileVersion
            || identity.Seed != 42
            || identity.RecordCount != RecordCount
            || !identity.Parameters.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .SequenceEqual(expectedGeneration.Parameters.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            || !identity.Dependencies.SequenceEqual(profile.Dependencies))
            throw new InvalidDataException("The imported dataset identity does not match the checked-out DataForge source and locked Spike 1 options.");

        string canonicalSha = ComputeCanonicalContentSha(target, profile);
        if (!string.Equals(canonicalSha, identity.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The imported records do not match their DataForge canonical content SHA-256.");
        return identity;
    }

    public static DataForgeDatasetIdentity ReadIdentity(SpikePaths paths)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(paths.IdentityPath));
        JsonElement root = document.RootElement;
        var dependencies = root.GetProperty("dependencies").EnumerateArray()
            .Select(static item => new DataForgeDependencyVersion(
                item.GetProperty("name").GetString()!,
                item.GetProperty("version").GetString()!))
            .ToArray();
        var parameters = root.GetProperty("parameters").EnumerateObject()
            .ToDictionary(static item => item.Name, static item => item.Value.GetString()!, StringComparer.Ordinal);
        return new DataForgeDatasetIdentity(
            DataForgeSourceKind.Generated,
            root.GetProperty("dataForgeVersion").GetInt32(),
            root.GetProperty("profileId").GetString(),
            root.GetProperty("profileVersion").GetInt32(),
            null,
            null,
            root.GetProperty("seed").GetUInt64(),
            root.GetProperty("recordCount").GetInt32(),
            parameters,
            dependencies,
            root.GetProperty("contentSha256").GetString()!);
    }

    public static string DatasetId(SpikePaths paths) => ReadIdentity(paths).GetShortKey();

    private static string ComputeCanonicalContentSha(SpikePaths paths, LeanCorpusSearchProfile profile)
    {
        using var writer = new CanonicalJsonWriter(Stream.Null);
        foreach (SearchRecord record in ReadRange(paths, 0, RecordCount))
        {
            profile.CanonicalRecordWriter.Write(writer, record);
            writer.WriteLine();
        }
        return Convert.ToHexString(writer.GetSha256()).ToLowerInvariant();
    }

    public static IEnumerable<SearchRecord> ReadRange(SpikePaths paths, int startOrdinal, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startOrdinal);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (startOrdinal > RecordCount - count)
            throw new ArgumentOutOfRangeException(nameof(startOrdinal));

        long start = ReadOffset(paths, startOrdinal);
        using var stream = new FileStream(paths.RecordsPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        stream.Position = start;
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false,
            bufferSize: 64 * 1024, leaveOpen: false);
        for (int index = 0; index < count; index++)
        {
            string line = reader.ReadLine() ?? throw new EndOfStreamException($"Dataset ended at ordinal {startOrdinal + index}.");
            yield return JsonSerializer.Deserialize<SearchRecord>(line, RecordJsonOptions)
                ?? throw new InvalidDataException($"Dataset record {startOrdinal + index} was null.");
        }
    }

    public static SearchRecord ReadRecord(SpikePaths paths, int ordinal)
        => ReadRange(paths, ordinal, 1).Single();

    public static void CreateCardinalityPayload(SpikePaths paths)
    {
        if (File.Exists(paths.PayloadPath))
            throw new IOException("The cardinality payload already exists. Refusing to replace it.");

        byte[] payload = GC.AllocateUninitializedArray<byte>(PayloadBytes);
        for (int index = 0; index < payload.Length; index++)
            payload[index] = (byte)((index * 31 + 17) & 0xff);

        using (var stream = new FileStream(paths.PayloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                   1024 * 1024, FileOptions.SequentialScan))
        {
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
        }

        string hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        File.WriteAllText(paths.PayloadShaPath, hash + "\n", new UTF8Encoding(false));
    }

    public static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static long ReadOffset(SpikePaths paths, int ordinal)
    {
        using var stream = new FileStream(paths.OffsetsPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            sizeof(long), FileOptions.RandomAccess);
        stream.Position = checked((long)ordinal * sizeof(long));
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        stream.ReadExactly(bytes);
        return System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }

    private static void WriteIdentity(
        string path,
        DataForgeDatasetIdentity identity,
        DataForgeGenerationOptions generation)
    {
        var json = new
        {
            sourceKind = "Generated",
            dataForgeVersion = identity.DataForgeVersion,
            profileId = identity.ProfileId,
            profileVersion = identity.ProfileVersion,
            seed = identity.Seed,
            recordCount = identity.RecordCount,
            generationOptions = "new DataForgeGenerationOptions(42, 100000)",
            parameters = identity.Parameters,
            dependencies = identity.Dependencies,
            contentSha256 = identity.ContentSha256,
            datasetId = identity.GetShortKey()
        };
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(path, JsonSerializer.Serialize(json, options) + "\n", new UTF8Encoding(false));
    }

    private static void BuildRecordOffsets(string recordsPath, string offsetsPath, int expectedCount)
    {
        long[] offsets = GC.AllocateUninitializedArray<long>(expectedCount);
        using var input = new FileStream(recordsPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        long bytePosition = 0;
        int lineCount = 0;
        offsets[0] = 0;
        try
        {
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int index = 0; index < read; index++)
                {
                    if (buffer[index] == (byte)'\n')
                    {
                        lineCount++;
                        if (lineCount < expectedCount)
                            offsets[lineCount] = bytePosition + index + 1;
                    }
                }
                bytePosition += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (lineCount != expectedCount)
            throw new InvalidDataException($"Canonical DataForge output contains {lineCount} lines; expected {expectedCount}.");

        using var output = new FileStream(offsetsPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.SequentialScan);
        Span<byte> encoded = stackalloc byte[sizeof(long)];
        foreach (long offset in offsets)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(encoded, offset);
            output.Write(encoded);
        }
        output.Flush(flushToDisk: true);
    }
}
