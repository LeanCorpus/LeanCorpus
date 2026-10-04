using Rowles.LeanCorpus.Store;

namespace Rowles.LeanCorpus.Codecs.Vectors;

/// <summary>Authoritative logical presence, independent of dense vector contents.</summary>
internal static class VectorPresence
{
    internal static int ByteCount(int docCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(docCount);
        return checked((docCount + 7) / 8);
    }

    internal static byte[] Create(int docCount, IEnumerable<int> docIds)
    {
        var bits = new byte[ByteCount(docCount)];
        foreach (int docId in docIds)
        {
            if ((uint)docId >= (uint)docCount)
                throw new InvalidDataException($"Vector document ID {docId} is outside [0, {docCount}).");
            byte mask = (byte)(1 << (docId & 7));
            if ((bits[docId >> 3] & mask) != 0)
                throw new InvalidDataException($"Duplicate vector document ID {docId}.");
            bits[docId >> 3] |= mask;
        }
        return bits;
    }

    internal static byte[] Read(IndexInput input, int docCount, ref long position, long bodyEnd)
    {
        int length = ByteCount(docCount);
        if (checked(position + length) > bodyEnd)
            throw new InvalidDataException("Truncated vector presence bitmap.");
        var bits = input.BorrowSpan(length, ref position).ToArray();
        int remainder = docCount & 7;
        if (remainder != 0 && (bits[^1] & (0xff << remainder)) != 0)
            throw new InvalidDataException("Vector presence bitmap has non-zero unused bits.");
        return bits;
    }

    internal static void ValidateVectors(int docCount, int dimension,
        IReadOnlyDictionary<int, ReadOnlyMemory<float>> vectors)
    {
        foreach (var (docId, vector) in vectors)
        {
            if ((uint)docId >= (uint)docCount || vector.Length != dimension)
                throw new InvalidDataException($"Invalid vector document ID {docId} or dimension {vector.Length}; expected {dimension}.");
        }
        _ = checked((long)docCount * dimension * sizeof(float) + ByteCount(docCount));
    }
}
