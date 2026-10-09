using Rowles.LeanCorpus.Analysis.Tokenisers;

namespace Rowles.LeanCorpus.Analysis.Filters;

/// <summary>
/// Truncates token text to a maximum Unicode scalar length, preserving original UTF-16 source offsets.
/// </summary>
public sealed class TruncateTokenFilter : ISpanTokenFilter
{
    private readonly int _maxLength;

    /// <summary>
    /// Initialises a new <see cref="TruncateTokenFilter"/> with the specified maximum length.
    /// </summary>
    /// <param name="maxLength">The maximum retained token text length in Unicode scalars.</param>
    public TruncateTokenFilter(int maxLength)
    {
        if (maxLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxLength));

        _maxLength = maxLength;
    }

    /// <inheritdoc/>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Apply(
        ReadOnlySpan<char> text,
        int startOffset,
        int endOffset,
        string type,
        int positionIncrement,
        byte[]? payload,
        ISpanTokenSink sink)
    {
        if (text.Length <= _maxLength)
        {
            sink.Add(text, startOffset, endOffset, type, positionIncrement, payload);
            return;
        }
        ApplyTruncated(text, startOffset, endOffset, type, positionIncrement, payload, sink);
    }

    private void ApplyTruncated(ReadOnlySpan<char> text, int startOffset, int endOffset,
        string type, int positionIncrement, byte[]? payload, ISpanTokenSink sink)
    {
        int boundary = UnicodeTokenisation.FindUtf16BoundaryAfterScalarUnits(text, _maxLength);
        sink.Add(text[..boundary], startOffset, endOffset, type, positionIncrement, payload);
    }
}
