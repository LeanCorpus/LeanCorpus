using System.Buffers;
using Rowles.LeanCorpus.Codecs.CodecKit;
using Rowles.LeanCorpus.Store;

namespace Rowles.LeanCorpus.Codecs.Vectors;

/// <summary>
/// Writes dense float vectors with a fixed-dimension layout for implicit offset indexing.
/// Body v2: [docCount:int32][dimension:int32][dataFormat:byte][presence bitmap][dense vector data].
/// </summary>
internal static class VectorWriter
{
    internal static void Write(string filePath, ReadOnlyMemory<float>[] vectors)
    {
        int dimension = 0;
        for (int i = 0; i < vectors.Length; i++)
        {
            if (vectors[i].Length > 0) { dimension = vectors[i].Length; break; }
        }

        var present = vectors.Select((vector, docId) => (vector, docId)).Where(item => item.vector.Length > 0).Select(item => item.docId).ToArray();
        foreach (int docId in present)
            if (vectors[docId].Length != dimension)
                throw new InvalidDataException("Vector dimensions do not match.");
        var presence = VectorPresence.Create(vectors.Length, present);
        CodecFileWriter.WriteAtomically(filePath, VectorCodecFiles.Float32, durable: false, bodyOutput =>
        {
            bodyOutput.WriteInt32(vectors.Length);
            bodyOutput.WriteInt32(dimension);
            bodyOutput.WriteByte(0); // data-format: float32
            bodyOutput.WriteBytes(presence, 0, presence.Length);

            Span<float> zero = dimension <= 256 ? stackalloc float[dimension] : new float[dimension];
            zero.Clear();

            for (int i = 0; i < vectors.Length; i++)
            {
                var span = vectors[i].Length == dimension ? vectors[i].Span : zero;
                for (int j = 0; j < dimension; j++)
                    bodyOutput.WriteSingle(span[j]);
            }
        });
    }

    /// <summary>
    /// Writes a per-field dense vector file. Missing docs are zero-padded so reader offset arithmetic
    /// remains valid; presence distinguishes these rows from explicitly supplied zero vectors.
    /// </summary>
    internal static void WriteField(
        string filePath,
        int docCount,
        int dimension,
        IReadOnlyDictionary<int, ReadOnlyMemory<float>> vectorsByDoc,
        VectorQuantisation quantisation = VectorQuantisation.None)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(docCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);

        if (quantisation is not VectorQuantisation.None and not VectorQuantisation.Int8)
            throw new ArgumentOutOfRangeException(nameof(quantisation));
        VectorPresence.ValidateVectors(docCount, dimension, vectorsByDoc);
        var presence = VectorPresence.Create(docCount, vectorsByDoc.Keys);
        CodecFileWriter.WriteAtomically(filePath, VectorCodecFiles.Float32, durable: false, bodyOutput =>
        {
            bodyOutput.WriteInt32(docCount);
            bodyOutput.WriteInt32(dimension);
            bodyOutput.WriteByte((byte)quantisation); // data-format byte: 0 = float32, 1 = int8
            bodyOutput.WriteBytes(presence, 0, presence.Length);

            Span<float> zero = dimension <= 256 ? stackalloc float[dimension] : new float[dimension];
            zero.Clear();

            if (quantisation == VectorQuantisation.Int8)
            {
                // Compute per-segment min/max
                float min = float.MaxValue, max = float.MinValue;
                foreach (var v in vectorsByDoc.Values)
                {
                    var sp = v.Span;
                    for (int j = 0; j < sp.Length; j++)
                    {
                        float val = sp[j];
                        if (val < min) min = val;
                        if (val > max) max = val;
                    }
                }
                if (MathF.Abs(max - min) < 1e-8f) max = min + 1f;
                float alpha = (max - min) / 255f;

                bodyOutput.WriteSingle(min);
                bodyOutput.WriteSingle(alpha);

                // Pack int8 bytes
                byte[] buf = ArrayPool<byte>.Shared.Rent(dimension);
                try
                {
                    for (int i = 0; i < docCount; i++)
                    {
                        ReadOnlySpan<float> span = zero;
                        if (vectorsByDoc.TryGetValue(i, out var v))
                        {
                            if (v.Length != dimension)
                                throw new InvalidDataException($"Vector for document {i} has dimension {v.Length}; expected {dimension}.");
                            span = v.Span;
                        }
                        for (int j = 0; j < dimension; j++)
                        {
                            float clamped = Math.Clamp((span[j] - min) / alpha + 0.5f, 0f, 255f);
                            buf[j] = (byte)clamped;
                        }
                        bodyOutput.WriteBytes(buf, 0, dimension);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buf, clearArray: false);
                }
            }
            else
            {
                for (int i = 0; i < docCount; i++)
                {
                    ReadOnlySpan<float> span = zero;
                    if (vectorsByDoc.TryGetValue(i, out var v))
                    {
                        if (v.Length != dimension)
                            throw new InvalidDataException($"Vector for document {i} has dimension {v.Length}; expected {dimension}.");
                        span = v.Span;
                    }
                    for (int j = 0; j < dimension; j++)
                        bodyOutput.WriteSingle(span[j]);
                }
            }
        });
    }

    /// <summary>Streams a dense per-field vector file from a random-access source.</summary>
    internal static void WriteField(
        string filePath,
        int docCount,
        int dimension,
        IVectorSource vectorsByDoc,
        IReadOnlyList<int> vectorDocIds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(docCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);
        ArgumentNullException.ThrowIfNull(vectorsByDoc);
        if (vectorsByDoc.Dimension != dimension || vectorsByDoc.Count < docCount)
            throw new ArgumentException("Vector source dimensions do not match the destination field.", nameof(vectorsByDoc));

        _ = checked(9L + VectorPresence.ByteCount(docCount) + (long)docCount * dimension * sizeof(float));
        var presence = VectorPresence.Create(docCount, vectorDocIds);
        float[] vectorBuffer = ArrayPool<float>.Shared.Rent(dimension);
        try
        {
            CodecFileWriter.WriteAtomically(filePath, VectorCodecFiles.Float32, durable: false, bodyOutput =>
            {
                bodyOutput.WriteInt32(docCount);
                bodyOutput.WriteInt32(dimension);
                bodyOutput.WriteByte((byte)VectorQuantisation.None);
                bodyOutput.WriteBytes(presence, 0, presence.Length);

                Span<float> vector = vectorBuffer.AsSpan(0, dimension);
                for (int docId = 0; docId < docCount; docId++)
                {
                    vectorsByDoc.CopyVectorTo(docId, vector);
                    for (int j = 0; j < dimension; j++)
                        bodyOutput.WriteSingle(vector[j]);
                }
            });
        }
        finally
        {
            ArrayPool<float>.Shared.Return(vectorBuffer, clearArray: false);
        }
    }
}
