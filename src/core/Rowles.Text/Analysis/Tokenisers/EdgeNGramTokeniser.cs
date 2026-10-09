namespace Rowles.LeanCorpus.Analysis.Tokenisers;

using Rowles.LeanCorpus.Analysis;

/// <summary>
/// Splits text into Unicode scalar substrings of length [<see cref="MinGram"/>, <see cref="MaxGram"/>]
/// anchored at the start of each whitespace-delimited token (edge n-grams), using
/// scalar whitespace classification for Unicode-aware whitespace detection.
///
/// Thread-safety: the span path and enumerator are thread-safe for concurrent use on the same instance.
/// No per-instance mutable state is retained across calls.
/// </summary>
public sealed class EdgeNGramTokeniser : IShareableSpanTokeniser
{
    /// <summary>
    /// Gets the minimum n-gram length in Unicode scalars (inclusive).
    /// </summary>
    public int MinGram { get; }

    /// <summary>
    /// Gets the maximum n-gram length in Unicode scalars (inclusive).
    /// </summary>
    public int MaxGram { get; }

    /// <summary>
    /// Initialises a new <see cref="EdgeNGramTokeniser"/> with the specified gram size range.
    /// </summary>
    /// <param name="minGram">The minimum gram length in Unicode scalars (must be ≥ 1).</param>
    /// <param name="maxGram">The maximum gram length in Unicode scalars (must be ≥ <paramref name="minGram"/>).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="minGram"/> is less than 1, or <paramref name="maxGram"/> is less than <paramref name="minGram"/>.
    /// </exception>
    public EdgeNGramTokeniser(int minGram, int maxGram)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minGram, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxGram, minGram);
        MinGram = minGram;
        MaxGram = maxGram;
    }

    /// <inheritdoc/>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Tokenise(ReadOnlySpan<char> input, ISpanTokenSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var enumerator = EnumerateTokens(input);
        while (enumerator.AdvanceBoundaries())
        {
            enumerator.EmitCurrent(sink);
        }
    }

    /// <summary>Enumerates scalar-sized grams without allocating, retaining UTF-16 source offsets.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public Enumerator EnumerateTokens(ReadOnlySpan<char> input) => new(this, input);

    /// <summary>The single boundary generator for both the sink and enumeration APIs.</summary>
    public ref struct Enumerator
    {
        private readonly int _minGram;
        private readonly int _maxGram;
        private readonly ReadOnlySpan<char> _input;
        private readonly bool _singleUnitInput;
        private readonly bool _asciiInput;
        private int _scanPos;
        private int _wordEnd;
        private int _start;
        private int _end;
        private int _units;
        private int _maximumPrefixUnits;
        private int _currentStart;
        private int _currentEnd;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal Enumerator(EdgeNGramTokeniser owner, ReadOnlySpan<char> input)
        {
            _minGram = owner.MinGram;
            _maxGram = owner.MaxGram;
            _input = input;
            _asciiInput = System.Text.Ascii.IsValid(input);
            _singleUnitInput = _asciiInput || !input.ContainsAnyInRange('\ud800', '\udfff');
            _scanPos = 0;
            _wordEnd = 0;
            _start = 0;
            _end = 0;
            _units = _singleUnitInput ? owner.MinGram - 1 : 0;
            _maximumPrefixUnits = 0;
            _currentStart = 0;
            _currentEnd = 0;
        }

        /// <summary>Gets the current token.</summary>
        public SpanToken Current
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            get => new(_input.Slice(_currentStart, _currentEnd - _currentStart), _currentStart, _currentEnd);
        }

        // The generator stores boundaries only. The sink does not need to create
        // or validate a SpanToken merely to forward the same default metadata.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal void EmitCurrent(ISpanTokenSink sink) =>
            sink.Add(_input[_currentStart.._currentEnd], _currentStart, _currentEnd);

        /// <summary>Advances to the next scalar-sized prefix.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            return AdvanceBoundaries();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool AdvanceBoundaries()
        {
            while (true)
            {
                if (NextPrefix()) return true;
                if (!ReadWord()) return false;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private bool NextPrefix()
        {
            if (_singleUnitInput)
            {
                if (_units >= _maximumPrefixUnits) return false;
                _units++;
                int end = _start + _units;
                _currentStart = _start;
                _currentEnd = end;
                return true;
            }
            while (_end < _wordEnd && _units < _maxGram)
            {
                _end += UnicodeTokenisation.GetScalarUnitWidth(_input, _end);
                _units++;
                if (_units >= _minGram)
                {
                    _currentStart = _start;
                    _currentEnd = _end;
                    return true;
                }
            }
            return false;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private bool ReadWord()
        {
            if (!UnicodeTokenisation.TryReadWhitespaceWord(_input, _asciiInput,
                ref _scanPos, out _start, out _wordEnd)) return false;
            _end = _start;
            _units = _singleUnitInput ? _minGram - 1 : 0;
            _maximumPrefixUnits = Math.Min(_maxGram, _wordEnd - _start);
            return true;
        }

        /// <summary>Returns this enumerator for foreach support.</summary>
        public Enumerator GetEnumerator() => this;
    }
}
