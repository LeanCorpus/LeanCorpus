using System.Text;

namespace Rowles.LeanCorpus.Analysis.Tokenisers;

/// <summary>Splits input into Unicode-scalar letter runs with UTF-16 source offsets.</summary>
public sealed class LetterTokeniser : IShareableSpanTokeniser
{
    /// <inheritdoc/>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Tokenise(ReadOnlySpan<char> input, ISpanTokenSink sink)
    {
        bool ascii = Ascii.IsValid(input);
        int position = 0;
        while (TryReadLetters(input, ascii, ref position, out int start, out int end))
            sink.Add(input[start..end], start, end);
    }

    /// <summary>Emits source offsets without materialising token text.</summary>
    internal void TokeniseOffsets(ReadOnlySpan<char> input, List<(int Start, int End)> offsets)
    {
        offsets.Clear();
        bool ascii = Ascii.IsValid(input);
        int position = 0;
        while (TryReadLetters(input, ascii, ref position, out int start, out int end))
            offsets.Add((start, end));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool TryReadLetters(ReadOnlySpan<char> input, bool ascii, ref int position, out int start, out int end)
    {
        int index = position;
        if (ascii)
        {
            while (index < input.Length && (uint)((input[index] | 0x20) - 'a') > 'z' - 'a') index++;
            start = index;
            while (index < input.Length && (uint)((input[index] | 0x20) - 'a') <= 'z' - 'a') index++;
        }
        else
        {
            while (index < input.Length)
            {
                bool letter = UnicodeTokenisation.TryDecodeRuneAt(input, index, out Rune rune, out int width)
                    && Rune.IsLetter(rune);
                if (letter) break;
                index += width;
            }
            start = index;
            while (index < input.Length && UnicodeTokenisation.TryDecodeRuneAt(input, index, out Rune rune, out int width)
                && Rune.IsLetter(rune)) index += width;
        }
        position = index;
        end = index;
        return end > start;
    }
}
