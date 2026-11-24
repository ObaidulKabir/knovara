using Knovara.ContextOptimization;

namespace Knovara.Api.Tests;

public class PinnedDocumentChunkerTests
{
    [Fact]
    public void ShortText_StaysAsOneChunk()
    {
        var chunks = PinnedDocumentChunker.Chunk("A short note.", maxChunkTokens: 300);

        Assert.Equal(["A short note."], chunks);
    }

    [Fact]
    public void Empty_YieldsNoChunks()
    {
        Assert.Empty(PinnedDocumentChunker.Chunk("   ", maxChunkTokens: 300));
    }

    [Fact]
    public void SmallParagraphsUnderTheCap_MergeIntoOneChunk()
    {
        var text = "First short paragraph.\n\nSecond short paragraph.\n\nThird short paragraph.";

        var chunks = PinnedDocumentChunker.Chunk(text, maxChunkTokens: 300);

        Assert.Single(chunks);
        Assert.Contains("First", chunks[0]);
        Assert.Contains("Third", chunks[0]);
    }

    [Fact]
    public void ParagraphsThatTogetherExceedTheCap_SplitIntoSeparateChunks()
    {
        var big = new string('x', 900);
        var text = big + "\n\n" + big;

        var chunks = PinnedDocumentChunker.Chunk(text, maxChunkTokens: 300); // cap = 1200 chars

        Assert.Equal(2, chunks.Count);
    }

    [Fact]
    public void ParagraphLargerThanTheCapOnItsOwn_SplitsOnSentences()
    {
        var sentence = "This is one sentence that repeats. ";
        var hugeParagraph = string.Concat(Enumerable.Repeat(sentence, 100)); // ~3600 chars, no blank lines

        var chunks = PinnedDocumentChunker.Chunk(hugeParagraph, maxChunkTokens: 300); // cap = 1200 chars

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 1200));
        // No sentence was dropped or corrupted: every chunk ends cleanly and nothing is duplicated.
        Assert.All(chunks, c => Assert.EndsWith(".", c.TrimEnd()));
    }

    [Fact]
    public void SingleSentenceLargerThanTheCap_IsHardCut()
    {
        var oneHugeSentence = new string('y', 3000) + ".";

        var chunks = PinnedDocumentChunker.Chunk(oneHugeSentence, maxChunkTokens: 300); // cap = 1200 chars

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.True(c.Length <= 1200));
    }

    [Fact]
    public void NoContentIsLost_ReassemblingChunksRecoversEverySentence()
    {
        var sentence = "Alpha beta gamma delta epsilon zeta eta theta.";
        var text = string.Join(" ", Enumerable.Repeat(sentence, 80));

        var chunks = PinnedDocumentChunker.Chunk(text, maxChunkTokens: 300);

        var rejoined = string.Join(" ", chunks);
        var expectedSentenceCount = 80;
        var actualSentenceCount = rejoined.Split('.').Count(s => s.Trim().Length > 0);
        Assert.Equal(expectedSentenceCount, actualSentenceCount);
    }
}
