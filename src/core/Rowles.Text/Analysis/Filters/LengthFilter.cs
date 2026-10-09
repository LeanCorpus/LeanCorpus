using Rowles.LeanCorpus.Analysis.Tokenisers;

namespace Rowles.LeanCorpus.Analysis.Filters;

/// <summary>
/// Removes tokens whose Unicode scalar length falls outside an inclusive range.
/// </summary>
public sealed class LengthFilter : ISpanTokenFilter
{
    private readonly int _minLength;
    private readonly int _maxLength;
    private readonly int _guaranteedMinimumUtf16Length;
    private readonly uint _guaranteedLengthRange;

    /// <summary>
    /// Initialises a new <see cref="LengthFilter"/> with inclusive minimum and maximum lengths.
    /// </summary>
    /// <param name="minLength">The minimum accepted token length in Unicode scalars.</param>
    /// <param name="maxLength">The maximum accepted token length in Unicode scalars.</param>
    public LengthFilter(int minLength, int maxLength)
    {
        if (minLength < 0)
            throw new ArgumentOutOfRangeException(nameof(minLength));
        if (maxLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxLength));
        if (maxLength < minLength)
            throw new ArgumentOutOfRangeException(nameof(maxLength));

        _minLength = minLength;
        _maxLength = maxLength;
        // 2 * min - 1 code units guarantee at least min scalar/invalid units.
        long guaranteedMinimum = minLength == 0 ? 0 : 2L * minLength - 1;
        if (guaranteedMinimum <= maxLength)
        {
            _guaranteedMinimumUtf16Length = (int)guaranteedMinimum;
            _guaranteedLengthRange = (uint)(maxLength - guaranteedMinimum);
        }
        else
        {
            // Subtracting this sentinel gives a non-zero unsigned value for
            // every possible span length, disabling the guaranteed range.
            _guaranteedMinimumUtf16Length = int.MinValue;
            _guaranteedLengthRange = 0;
        }
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
        if (unchecked((uint)(text.Length - _guaranteedMinimumUtf16Length)) <= _guaranteedLengthRange)
        {
            sink.Add(text, startOffset, endOffset, type, positionIncrement, payload);
            return;
        }
        ApplyCounted(text, startOffset, endOffset, type, positionIncrement, payload, sink);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void ApplyCounted(ReadOnlySpan<char> text, int startOffset, int endOffset,
        string type, int positionIncrement, byte[]? payload, ISpanTokenSink sink)
    {
        if (text.Length < _minLength) return;
        int limit = _maxLength == int.MaxValue ? int.MaxValue : _maxLength + 1;
        int len = UnicodeTokenisation.CountScalarUnitsUpTo(text, limit);
        if (len >= _minLength && len <= _maxLength)
            sink.Add(text, startOffset, endOffset, type, positionIncrement, payload);
    }
}
