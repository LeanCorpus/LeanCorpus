namespace Rowles.LeanCorpus.Analysis.Tokenisers;

using Rowles.LeanCorpus.Analysis;

/// <summary>
/// Splits text into all contiguous Unicode scalar substrings of length in [<see cref="MinGram"/>, <see cref="MaxGram"/>].
/// Useful for partial-word matching and CJK text.
///
/// When <see cref="SplitOnWhitespace"/> is <see langword="true"/> the tokeniser first splits on
/// whitespace (via scalar whitespace classification) and applies n-grams per word only,
/// which avoids cross-word-boundary grams.
///
/// Thread-safety: the span path and enumerator are thread-safe for concurrent use on the same instance.
/// No per-instance mutable state is retained across calls.
/// </summary>
public sealed class NGramTokeniser : IShareableSpanTokeniser
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
    /// Gets whether the tokeniser splits on whitespace before applying n-grams.
    /// When <see langword="true"/>, no gram spans a word boundary.
    /// </summary>
    public bool SplitOnWhitespace { get; }

    /// <summary>
    /// Initialises a new <see cref="NGramTokeniser"/> with the specified gram size range.
    /// </summary>
    /// <param name="minGram">The minimum gram length in Unicode scalars (must be ≥ 1).</param>
    /// <param name="maxGram">The maximum gram length in Unicode scalars (must be ≥ <paramref name="minGram"/>).</param>
    /// <param name="splitOnWhitespace">
    /// When <see langword="true"/>, n-grams are generated per whitespace-delimited word rather than
    /// across the entire input. Defaults to <see langword="false"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="minGram"/> is less than 1, or <paramref name="maxGram"/> is less than <paramref name="minGram"/>.
    /// </exception>
    public NGramTokeniser(int minGram, int maxGram, bool splitOnWhitespace = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minGram, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxGram, minGram);
        MinGram = minGram;
        MaxGram = maxGram;
        SplitOnWhitespace = splitOnWhitespace;
    }

    /// <inheritdoc/>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Tokenise(ReadOnlySpan<char> input, ISpanTokenSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var enumerator = EnumerateTokens(input);
        enumerator.EmitTo(sink);
    }

    /// <summary>Enumerates scalar-sized grams without allocating, retaining UTF-16 source offsets.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public Enumerator EnumerateTokens(ReadOnlySpan<char> input) => new(this, input);

    /// <summary>The single boundary generator for both the sink and enumeration APIs.</summary>
    public ref struct Enumerator
    {
        private readonly int _minGram;
        private readonly int _maxGram;
        private readonly bool _splitOnWhitespace;
        private readonly ReadOnlySpan<char> _input;
        private readonly bool _singleUnitInput;
        private readonly bool _asciiInput;
        private int _scanPos;
        private int _wordEnd;
        private int _start;
        private int _end;
        private int _units;
        private bool _haveWord;
        private int _currentStart;
        private int _currentEnd;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal Enumerator(NGramTokeniser owner, ReadOnlySpan<char> input)
        {
            _minGram = owner.MinGram;
            _maxGram = owner.MaxGram;
            _splitOnWhitespace = owner.SplitOnWhitespace;
            _input = input;
            _asciiInput = System.Text.Ascii.IsValid(input);
            _singleUnitInput = _asciiInput || !input.ContainsAnyInRange('\ud800', '\udfff');
            _scanPos = 0;
            _wordEnd = owner.SplitOnWhitespace ? 0 : input.Length;
            _start = 0;
            _end = 0;
            _units = _singleUnitInput ? owner.MinGram - 1 : 0;
            _haveWord = !owner.SplitOnWhitespace;
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

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal void EmitTo(ISpanTokenSink sink)
        {
            // Dispatch once for the common full-input single-unit case. Both
            // entry points still call exactly the same boundary generator.
            if (!_splitOnWhitespace && _singleUnitInput)
            {
                while (NextSingleUnitGram(_input.Length)) EmitCurrent(sink);
                return;
            }
            while (AdvanceBoundaries()) EmitCurrent(sink);
        }

        /// <summary>Advances to the next scalar-sized gram.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            return AdvanceBoundaries();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool AdvanceBoundaries()
        {
            if (!_splitOnWhitespace) return NextInWord(_input.Length);
            while (true)
            {
                if (!_haveWord && !ReadWord()) return false;
                if (NextInWord(_wordEnd)) return true;
                _haveWord = false;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private bool NextInWord(int wordEnd)
        {
            if (_singleUnitInput) return NextSingleUnitGram(wordEnd);
            while (_start < wordEnd)
            {
                if (_end < wordEnd && _units < _maxGram)
                {
                    _end += UnicodeTokenisation.GetScalarUnitWidth(_input, _end);
                    _units++;
                    if (_units >= _minGram)
                    {
                        _currentStart = _start;
                        _currentEnd = _end;
                        return true;
                    }
                    continue;
                }
                _start += UnicodeTokenisation.GetScalarUnitWidth(_input, _start);
                _end = _start;
                _units = 0;
            }
            return false;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private bool NextSingleUnitGram(int wordEnd)
        {
            while (_start < wordEnd)
            {
                _units++;
                if (_units <= _maxGram && _units <= wordEnd - _start)
                {
                    int end = _start + _units;
                    _currentStart = _start;
                    _currentEnd = end;
                    return true;
                }
                _start++;
                _units = _minGram - 1;
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
            _haveWord = true;
            return true;
        }

        /// <summary>Returns this enumerator for foreach support.</summary>
        public Enumerator GetEnumerator() => this;
    }
}
