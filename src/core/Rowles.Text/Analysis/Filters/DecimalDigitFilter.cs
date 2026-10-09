using System.Buffers;
using System.Globalization;
using System.Text;
using Rowles.LeanCorpus.Analysis.Tokenisers;

namespace Rowles.LeanCorpus.Analysis.Filters;

/// <summary>
/// Normalises Unicode decimal digits to ASCII digits.
/// </summary>
public sealed class DecimalDigitFilter : ISpanTokenFilter
{
    private const int StackThreshold = 128;

    /// <inheritdoc/>
    public void Apply(
        ReadOnlySpan<char> text,
        int startOffset,
        int endOffset,
        string type,
        int positionIncrement,
        byte[]? payload,
        ISpanTokenSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        int index = IndexOfNormalisableDigit(text);
        if (index < 0)
        {
            sink.Add(text, startOffset, endOffset, type, positionIncrement, payload);
            return;
        }

        char[]? rented = null;
        try
        {
            Span<char> buffer = text.Length <= StackThreshold
                ? stackalloc char[text.Length]
                : (rented = ArrayPool<char>.Shared.Rent(text.Length));

            text[..index].CopyTo(buffer);
            int write = index;
            for (int i = index; i < text.Length;)
            {
                if (UnicodeTokenisation.TryDecodeRuneAt(text, i, out Rune rune, out int width))
                {
                    if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber &&
                        Rune.GetNumericValue(rune) is double value && value is >= 0 and <= 9 && value == Math.Truncate(value))
                        buffer[write++] = (char)('0' + (int)value);
                    else
                        write += rune.EncodeToUtf16(buffer[write..]);
                }
                else
                    buffer[write++] = text[i];
                i += width;
            }
            sink.Add(buffer[..write], startOffset, endOffset, type, positionIncrement, payload);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static int IndexOfNormalisableDigit(ReadOnlySpan<char> text)
    {
        // ASCII cannot need decimal normalisation; skip it with the span search.
        if (Ascii.IsValid(text)) return -1;
        int firstNonAscii = text.IndexOfAnyInRange((char)128, char.MaxValue);
        if (firstNonAscii < 0) return -1;
        for (int i = firstNonAscii; i < text.Length;)
        {
            bool valid = UnicodeTokenisation.TryDecodeRuneAt(text, i, out Rune rune, out int width);
            if (valid && rune.Value > 127 && Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber)
                return i;
            i += width;
        }
        return -1;
    }
}
