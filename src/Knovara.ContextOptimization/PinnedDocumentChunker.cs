using System.Text;
using System.Text.RegularExpressions;

namespace Knovara.ContextOptimization;

/// <summary>
/// Splits a long pinned (user-attached) document into passage-sized chunks on structural boundaries
/// (paragraphs, then sentences) - the same principle system_architecture.md states for ingested
/// sources - so a large attachment becomes retrievable pieces instead of one opaque, all-or-nothing
/// blob that TokenBudgetContextOptimizationService can only include whole or drop whole.
/// </summary>
public static class PinnedDocumentChunker
{
    /// <summary>Matches TokenBudgetContextOptimizationService's own estimate, so a chunk's size lines up with the budget it competes for.</summary>
    private const int CharsPerTokenEstimate = 4;

    public const int DefaultMaxChunkTokens = 300;

    public static IReadOnlyList<string> Chunk(string text, int maxChunkTokens = DefaultMaxChunkTokens)
    {
        var maxChars = maxChunkTokens * CharsPerTokenEstimate;
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return [];
        if (trimmed.Length <= maxChars) return [trimmed];

        var paragraphs = Regex.Split(trimmed, @"\n\s*\n").Select(p => p.Trim()).Where(p => p.Length > 0);
        var chunks = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }
        }

        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Length > maxChars)
            {
                Flush();
                chunks.AddRange(SplitLongParagraph(paragraph, maxChars));
                continue;
            }

            if (current.Length > 0 && current.Length + 2 + paragraph.Length > maxChars) Flush();
            if (current.Length > 0) current.Append("\n\n");
            current.Append(paragraph);
        }
        Flush();
        return chunks;
    }

    /// <summary>A paragraph too big on its own splits on sentences; a single sentence still too big is cut at the limit.</summary>
    private static IEnumerable<string> SplitLongParagraph(string paragraph, int maxChars)
    {
        var sentences = Regex.Split(paragraph, @"(?<=[.!?])\s+");
        var current = new StringBuilder();

        foreach (var rawSentence in sentences)
        {
            var piece = rawSentence;
            while (piece.Length > maxChars)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString().Trim();
                    current.Clear();
                }
                yield return piece[..maxChars];
                piece = piece[maxChars..];
            }

            if (current.Length > 0 && current.Length + 1 + piece.Length > maxChars)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(piece);
        }
        if (current.Length > 0) yield return current.ToString().Trim();
    }
}
