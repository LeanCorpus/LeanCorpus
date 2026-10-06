using System.Text.RegularExpressions;

namespace Rowles.LeanCorpus.Tests.Core.Index.Indexer;

[Category(TestCategory.Unit)]
[Area(TestArea.Index)]
public sealed class CommitManagerDurabilityInstrumentationTests
{
    [Fact(DisplayName = "Durability candidate observation reuses the persistence loop file-length read")]
    public void SyncChangedFiles_DoesNotReadCandidateLengthsBeforePersistence()
    {
        string sourcePath = FindRepositoryFile("src/core/Rowles.LeanCorpus/Index/Indexer/CommitManager.cs");
        string source = File.ReadAllText(sourcePath);
        string body = ExtractMethodBody(source, "private static void SyncChangedFiles(IndexWriter writer)");

        MatchCollection lengthReads = Regex.Matches(body, @"FileOpenRetry\.GetFileLength\s*\(");
        Assert.Single(lengthReads);

        int syncIndex = body.IndexOf("DirectoryFsync.SyncFile(", StringComparison.Ordinal);
        int lengthIndex = lengthReads[0].Index;
        Assert.True(syncIndex >= 0 && syncIndex < lengthIndex,
            "The sole file-length read must remain after the real SyncFile call.");

        int loopStart = body.IndexOf("foreach (var dirtyFile in dirtyFiles)", StringComparison.Ordinal);
        Assert.True(loopStart >= 0, "Could not find the single dirty-file persistence loop.");
        int loopOpenBrace = body.IndexOf('{', loopStart);
        int loopEnd = FindBlockEnd(body, loopOpenBrace);
        int candidateCountIndex = body.IndexOf("candidateCount++", loopStart, StringComparison.Ordinal);
        int candidateObservationIndex = body.IndexOf("SpikeInstrumentationPoint.DurabilityCandidates", StringComparison.Ordinal);
        Assert.True(loopStart >= 0 && candidateCountIndex > loopStart && candidateCountIndex < syncIndex,
            "Non-temporary candidates must be counted in memory before their real persistence request.");
        Assert.True(candidateObservationIndex > loopEnd,
            "Candidate counters must be emitted after the single real persistence loop.");
        Assert.Contains("value: candidateCount", body, StringComparison.Ordinal);
        Assert.Contains("amount: bytes", body, StringComparison.Ordinal);
        Assert.DoesNotContain("new List<DirtyFileTracker.DirtyFile>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("filesToSync", body, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException($"Could not find repository source '{relativePath}' from '{AppContext.BaseDirectory}'.");
    }

    private static string ExtractMethodBody(string source, string signature)
    {
        int signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"Could not find method signature '{signature}'.");
        int openBrace = source.IndexOf('{', signatureIndex);
        Assert.True(openBrace >= 0, $"Could not find the body for '{signature}'.");
        int closeBrace = FindBlockEnd(source, openBrace);
        return source[(openBrace + 1)..closeBrace];
    }

    private static int FindBlockEnd(string source, int openBrace)
    {
        Assert.True(openBrace >= 0 && openBrace < source.Length && source[openBrace] == '{');
        int depth = 0;
        for (int index = openBrace; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return index;
        }

        throw new InvalidDataException("The source block has no matching closing brace.");
    }
}
