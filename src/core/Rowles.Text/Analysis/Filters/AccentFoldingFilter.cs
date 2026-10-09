using System.Buffers;
using System.Globalization;
using System.Text;
using Rowles.LeanCorpus.Analysis.Tokenisers;

namespace Rowles.LeanCorpus.Analysis.Filters;

/// <summary>
/// Folds accents by canonical decomposition, removal of non-spacing-mark scalars,
/// and canonical composition. This is not general ASCII transliteration.
/// Isolated surrogate units are preserved between independently normalised valid runs.
/// </summary>
public sealed class AccentFoldingFilter : ISpanTokenFilter
{
    private const int StackThreshold = 128;

    /// <inheritdoc/>
    public void Apply(ReadOnlySpan<char> text, int startOffset, int endOffset,
        string type, int positionIncrement, byte[]? payload, ISpanTokenSink sink)
    {
        if (IsAscii(text))
        {
            sink.Add(text, startOffset, endOffset, type, positionIncrement, payload);
            return;
        }

        // FormD lengths bound the final composed output, including invalid units.
        int capacity = 0;
        int position = 0;
        while (position < text.Length)
        {
            int start = position;
            AdvanceValidRun(text, ref position);
            capacity = checked(capacity + text[start..position].GetNormalizedLength(NormalizationForm.FormD));
            if (position < text.Length)
            {
                capacity = checked(capacity + 1);
                position++;
            }
        }

        char[]? rented = null;
        Span<char> output = capacity <= StackThreshold
            ? stackalloc char[capacity]
            : (rented = ArrayPool<char>.Shared.Rent(capacity));
        try
        {
            int written = 0;
            position = 0;
            while (position < text.Length)
            {
                int start = position;
                AdvanceValidRun(text, ref position);
                written += FoldValidRun(text[start..position], output[written..]);
                if (position < text.Length)
                    output[written++] = text[position++];
            }
            sink.Add(output[..written], startOffset, endOffset, type, positionIncrement, payload);
        }
        finally
        {
            if (rented is not null) ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static void AdvanceValidRun(ReadOnlySpan<char> text, ref int position)
    {
        while (UnicodeTokenisation.TryDecodeRuneAt(text, position, out _, out int width))
            position += width;
    }

    private static bool IsAscii(ReadOnlySpan<char> text) =>
        Ascii.IsValid(text);

    private static int FoldValidRun(ReadOnlySpan<char> text, Span<char> output)
    {
        if (IsAscii(text))
        {
            text.CopyTo(output);
            return text.Length;
        }
        int length = text.GetNormalizedLength(NormalizationForm.FormD);
        char[]? rented = null;
        Span<char> decomposed = length <= StackThreshold
            ? stackalloc char[length]
            : (rented = ArrayPool<char>.Shared.Rent(length));
        try
        {
            if (!text.TryNormalize(decomposed, out int written, NormalizationForm.FormD))
                throw new InvalidOperationException("The decomposition buffer is too small.");
            int retained = 0;
            for (int position = 0; position < written;)
            {
                UnicodeTokenisation.TryDecodeRuneAt(decomposed[..written], position, out var rune, out int width);
                if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.NonSpacingMark)
                {
                    decomposed.Slice(position, width).CopyTo(decomposed[retained..]);
                    retained += width;
                }
                position += width;
            }
            int composedLength = ((ReadOnlySpan<char>)decomposed[..retained]).GetNormalizedLength(NormalizationForm.FormC);
            if (!((ReadOnlySpan<char>)decomposed[..retained]).TryNormalize(output[..composedLength], out written, NormalizationForm.FormC))
                throw new InvalidOperationException("The composition buffer is too small.");
            return written;
        }
        finally
        {
            if (rented is not null) ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>Convenience wrapper returning the original reference when unchanged.</summary>
    internal static string Fold(string input)
    {
        var sink = new StringResultSink(input);
        new AccentFoldingFilter().Apply(input, 0, input.Length, Token.DefaultType, 1, null, sink);
        return sink.Result;
    }

    private sealed class StringResultSink : ISpanTokenSink
    {
        private readonly string _original;
        public string Result { get; private set; }
        public StringResultSink(string original) { _original = original; Result = original; }
        public void Add(ReadOnlySpan<char> text, int startOffset, int endOffset,
            string type = Token.DefaultType, int positionIncrement = 1, byte[]? payload = null)
        {
            if (!text.SequenceEqual(_original.AsSpan())) Result = new string(text);
        }
    }
}
