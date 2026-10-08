using System.Text;
using Rowles.LeanCorpus.Codecs.CodecKit;
using Rowles.LeanCorpus.Document.Fields;
using Rowles.LeanCorpus.Store;

namespace Rowles.LeanCorpus.Codecs.DocValues;

/// <summary>Reads bounded exact token counts, including legacy VarInt bodies.</summary>
internal static class FieldLengthReader
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public static Dictionary<string, int[]>? TryRead(string path, int expectedDocumentCount)
        => FileOpenRetry.FileExists(path) ? TryRead(new IndexInput(path), expectedDocumentCount) : null;

    internal static Dictionary<string, int[]> TryRead(IndexInput input, int expectedDocumentCount)
        => Decode(input, expectedDocumentCount, materialise: true);

    internal static void Validate(IndexInput input, int expectedDocumentCount)
        => Decode(input, expectedDocumentCount, materialise: false);

    internal static List<(string Name, int[] Lengths)> EnumerateFields(string path, int expectedDocumentCount)
        => TryRead(path, expectedDocumentCount)?.Select(static field => (field.Key, field.Value)).ToList() ?? [];

    private static Dictionary<string, int[]> Decode(IndexInput input, int expectedDocumentCount, bool materialise)
    {
        using var inputLifetime = input;
        try
        {
            if (expectedDocumentCount < 0)
                throw new InvalidDataException("The segment document count must be non-negative.");
            var descriptor = CodecCatalog.Default.GetFile("leancorpus.field-lengths.data");
            using var frame = CodecFileReader.OpenSupported(input, descriptor);
            using var body = frame.OpenBodyInput();
            RequireBytes(body, sizeof(int));
            int fieldCount = body.ReadInt32();
            long valueBytes = checked((long)expectedDocumentCount * sizeof(int));
            long minimumValueBytes = frame.FormatVersion >= 3 ? valueBytes : expectedDocumentCount;
            long minimumRecordBytes = checked(2L * sizeof(int) + 1 + minimumValueBytes);
            if (fieldCount < 0 || fieldCount > (body.Length - body.Position) / minimumRecordBytes)
                throw new InvalidDataException("Field count cannot fit in the field-length body.");
            // Never use an untrusted field count as a collection capacity.
            var result = new Dictionary<string, int[]>(StringComparer.Ordinal);
            for (int field = 0; field < fieldCount; field++)
            {
                RequireBytes(body, sizeof(int));
                int nameLength = body.ReadInt32();
                long reserved = checked(sizeof(int) + minimumValueBytes + (fieldCount - field - 1L) * minimumRecordBytes);
                if (nameLength <= 0 || nameLength > body.Length - body.Position - reserved)
                    throw new InvalidDataException("Field name length cannot fit in the field-length body.");
                string name = Utf8.GetString(body.BorrowSpan(nameLength));
                FieldNameValidator.Validate(name, nameof(name));
                if (result.ContainsKey(name))
                    throw new InvalidDataException("Duplicate field name in field-length body.");
                RequireBytes(body, sizeof(int));
                int documentCount = body.ReadInt32();
                if (documentCount < expectedDocumentCount ||
                    (frame.FormatVersion >= 3 && documentCount != expectedDocumentCount))
                    throw new InvalidDataException("Field-length document count differs from its owning segment.");
                long encodedMinimum = frame.FormatVersion >= 3 ? valueBytes : documentCount;
                long followingRecords = checked((fieldCount - field - 1L) * minimumRecordBytes);
                RequireBytes(body, checked(encodedMinimum + followingRecords));
                // Validate legacy VarInts before allocating their decoded Int32 array.
                long valuesStart = body.Position;
                for (int document = 0; document < documentCount; document++)
                {
                    int value = ReadValue(body, frame.FormatVersion);
                    if (value < 0)
                        throw new InvalidDataException("Field lengths must be non-negative.");
                    if (document >= expectedDocumentCount && value != 0)
                        throw new InvalidDataException("Legacy field-length padding must be zero.");
                }
                long valuesEnd = body.Position;
                int[] lengths = [];
                if (materialise)
                {
                    if (valueBytes > (long)Array.MaxLength * sizeof(int))
                        throw new InvalidDataException("Field-length array exceeds the supported representation.");
                    lengths = new int[expectedDocumentCount];
                    body.Seek(valuesStart);
                    for (int document = 0; document < expectedDocumentCount; document++)
                        lengths[document] = ReadValue(body, frame.FormatVersion);
                    body.Seek(valuesEnd);
                }
                result.Add(name, lengths);
            }
            if (body.Position != body.Length)
                throw new InvalidDataException("Trailing bytes in field-length body.");
            frame.ValidateChecksum();
            return result;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or EndOfStreamException or FormatException or CodecFileException)
        {
            throw new InvalidDataException("Invalid field-length metadata.", exception);
        }
    }

    private static int ReadValue(IndexInput body, int version)
        => version >= 3 ? body.ReadInt32() : body.ReadVarInt();

    private static void RequireBytes(IndexInput body, long count)
    {
        if (count < 0 || count > body.Length - body.Position)
            throw new InvalidDataException("Truncated field-length body.");
    }
}
