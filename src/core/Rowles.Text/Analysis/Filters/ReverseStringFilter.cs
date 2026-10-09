using System.Buffers;
using Rowles.LeanCorpus.Analysis.Tokenisers;

namespace Rowles.LeanCorpus.Analysis.Filters;

/// <summary>
/// Reverses Unicode scalars in each token, preserving isolated surrogate units. This is not grapheme reversal.
/// </summary>
public sealed class ReverseStringFilter : ISpanTokenFilter
{
    private const int StackallocThreshold = 128;

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
        if (text.Length <= StackallocThreshold)
        {
            Span<char> reversed = stackalloc char[text.Length];
            ReverseInto(text, reversed);
            sink.Add(reversed, startOffset, endOffset, type, positionIncrement, payload);
            return;
        }
        char[] rented = ArrayPool<char>.Shared.Rent(text.Length);
        try
        {
            Span<char> reversed = rented.AsSpan(0, text.Length);
            ReverseInto(text, reversed);
            sink.Add(reversed, startOffset, endOffset, type, positionIncrement, payload);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void ReverseInto(ReadOnlySpan<char> text, Span<char> reversed)
    {
        // Prove all units are complete BMP scalars before reversing code units.
        if (System.Text.Ascii.IsValid(text) || !text.ContainsAnyInRange('\ud800', '\udfff'))
        {
            text.CopyTo(reversed);
            reversed.Reverse();
            return;
        }
        ReverseScalars(text, reversed);
    }

    private static void ReverseScalars(ReadOnlySpan<char> text, Span<char> reversed)
    {
        int readEnd = text.Length;
        int write = 0;
        while (readEnd > 0)
        {
            if (UnicodeTokenisation.TryDecodeRuneBefore(text, readEnd, out var rune, out int width))
                write += rune.EncodeToUtf16(reversed[write..]);
            else
                reversed[write++] = text[readEnd - 1];
            readEnd -= width;
        }
    }
}
