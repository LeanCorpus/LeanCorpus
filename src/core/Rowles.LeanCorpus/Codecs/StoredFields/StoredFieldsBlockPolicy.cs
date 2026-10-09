namespace Rowles.LeanCorpus.Codecs.StoredFields;

/// <summary>
/// Shared raw and document limits, with version-aware encoded limits for stored-fields blocks.
/// Raw/decompressed and encoded byte ceilings are independent absolute bounds.
/// </summary>
internal static class StoredFieldsBlockPolicy
{
    internal const int TargetRawBytes = 1024 * 1024;
    internal const int MaximumRawBytes = 256 * 1024 * 1024;
    internal const int MaximumEncodedBytes = 320 * 1024 * 1024;
    internal const int MaximumDocumentCount = 100_000;

    internal static void ValidateRawLength(long rawLength)
    {
        if (rawLength < 0 || rawLength > MaximumRawBytes)
            throw new InvalidDataException(
                $"Stored fields raw length {rawLength} exceeds the maximum block size {MaximumRawBytes}.");
    }

    internal static void ValidateEncodedLength(int encodedLength, int formatVersion)
    {
        int maximumEncodedBytes = formatVersion switch
        {
            >= 1 and <= 4 => MaximumRawBytes,
            5 => MaximumEncodedBytes,
            _ => throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "Encoded block limits are defined only for stored-fields versions 1 through 5.")
        };
        if (encodedLength <= 0 || encodedLength > maximumEncodedBytes)
            throw new InvalidDataException(
                $"Stored fields block compLength {encodedLength} exceeds maximum {maximumEncodedBytes}.");
    }

    internal static bool IsValidMaximumDocumentCount(int maximumDocumentCount)
        => maximumDocumentCount is >= 1 and <= MaximumDocumentCount;

    internal static void ValidateMaximumDocumentCount(int maximumDocumentCount, string? parameterName = null)
    {
        if (!IsValidMaximumDocumentCount(maximumDocumentCount))
            throw new ArgumentOutOfRangeException(
                parameterName ?? nameof(maximumDocumentCount), maximumDocumentCount,
                $"Stored fields block document limit must be in the range [1, {MaximumDocumentCount}].");
    }

    internal static bool ShouldFlushBeforeAdd(
        int documentCount,
        int rawBytes,
        long nextDocumentRawBytes,
        int maximumDocumentCount)
        => documentCount > 0 &&
            (documentCount >= maximumDocumentCount || nextDocumentRawBytes > TargetRawBytes - (long)rawBytes);

    internal static bool ShouldFlushAfterAdd(int documentCount, int rawBytes, int maximumDocumentCount)
        => documentCount >= maximumDocumentCount || rawBytes >= TargetRawBytes;
}
