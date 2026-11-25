using System.Text;
using System.Text.RegularExpressions;
using Knovara.Core.Models;

namespace Knovara.Local.Ingestion;

/// <summary>What an extractor reads out of a source, in reading order.</summary>
internal abstract record Block;

internal sealed record HeadingBlock(int Level, string Text) : Block;

/// <summary>A piece of text. Standalone blocks (code, tables) never merge with their neighbours. Items of one list share a ListKey.</summary>
internal sealed record TextBlock(string Level, string Text, bool Standalone = false, string? ListKey = null) : Block;

/// <summary>
/// Turns blocks into the document -> section -> passage hierarchy, with the same passage rules as the Docling worker
/// (workers/document-processing/worker.py): one passage per structural unit, units too small to be found on their own are
/// joined to their neighbours, oversized units are split on sentence boundaries, and each passage's title is its heading path.
/// Sizes are estimated at 4 characters per token (the worker counts real embedding-model tokens), which is close enough for
/// chunking; the embedder truncates at 512 tokens regardless.
/// </summary>
internal sealed partial class HierarchyAssembler(string sourceName, int maxChunkTokens = 384, int minChunkTokens = 64)
{
    private const int MaxTitleChars = 1000;

    private readonly List<ExtractedNode> _nodes = [];
    private readonly List<(int Level, string Id, string Text)> _sections = [];
    private readonly List<Unit> _pending = [];
    private bool _titled, _contentSeen;

    private sealed record Unit(string Level, string Text, int Tokens, string? ListKey);

    public IReadOnlyList<ExtractedNode> Build(IEnumerable<Block> blocks, string? documentTitle = null)
    {
        _nodes.Add(new ExtractedNode("n0", null, "document", Clip(documentTitle ?? sourceName), null, new Dictionary<string, string> { ["source"] = sourceName }));
        _titled = documentTitle is not null;

        foreach (var block in blocks)
        {
            switch (block)
            {
                case HeadingBlock h: AddHeading(h); break;
                case TextBlock t: AddText(t); break;
            }
        }

        Flush();
        return _nodes;
    }

    private void AddHeading(HeadingBlock heading)
    {
        var text = heading.Text.Trim();
        if (text.Length == 0) return;

        // A level-1 heading that opens the document is its title (what Docling reports as the title item), not a section.
        if (heading.Level == 1 && !_titled && !_contentSeen && _sections.Count == 0)
        {
            _nodes[0] = _nodes[0] with { Title = Clip(text) };
            _titled = true;
            return;
        }

        Flush();
        while (_sections.Count > 0 && _sections[^1].Level >= heading.Level) _sections.RemoveAt(_sections.Count - 1);

        var id = NextId();
        _nodes.Add(new ExtractedNode(id, ParentId(), "section", Clip($"{HeadingPath()} > {text}"), null,
            new Dictionary<string, string> { ["headingLevel"] = heading.Level.ToString() }));
        _sections.Add((heading.Level, id, text));
    }

    private void AddText(TextBlock block)
    {
        var text = block.Text.Trim();
        if (text.Length == 0) return;
        _contentSeen = true;

        if (block.Standalone) Flush();
        foreach (var piece in Split(text, block.Standalone && block.Level == "table"))
        {
            if (block.Standalone) AddPassage(block.Level, piece, new Dictionary<string, string> { ["label"] = block.Level });
            else _pending.Add(new Unit(block.Level, piece, Tokens(piece), block.ListKey));
        }
    }

    private void Flush()
    {
        var groups = new List<List<Unit>>();
        foreach (var unit in _pending)
        {
            if (groups.Count > 0 && Joinable(groups[^1], unit)) groups[^1].Add(unit);
            else groups.Add([unit]);
        }
        _pending.Clear();

        foreach (var group in groups)
        {
            if (group.Count == 1)
            {
                AddPassage(group[0].Level, group[0].Text, new Dictionary<string, string> { ["label"] = group[0].Level });
                continue;
            }

            var levels = group.Select(u => u.Level).Distinct().ToList();
            var level = levels.Count == 1 && levels[0] == "list_item" ? "list" : levels.Count == 1 ? levels[0] : "text";
            AddPassage(level, string.Join("\n", group.Select(u => u.Text)),
                new Dictionary<string, string> { ["label"] = string.Join(",", levels), ["merged"] = group.Count.ToString() });
        }
    }

    // A unit under the minimum (an author line, a one-line paragraph) carries too little meaning to be found on its own;
    // two units that are each big enough stay separate passages.
    private bool Joinable(List<Unit> group, Unit unit)
    {
        var size = group.Sum(u => u.Tokens);
        if (size + unit.Tokens > maxChunkTokens) return false;
        var sameList = unit.ListKey is not null && unit.ListKey == group[^1].ListKey;
        return sameList || size < minChunkTokens || unit.Tokens < minChunkTokens;
    }

    private void AddPassage(string level, string content, Dictionary<string, string> metadata) =>
        _nodes.Add(new ExtractedNode(NextId(), ParentId(), level, Clip(HeadingPath()), content, metadata));

    private string NextId() => "n" + _nodes.Count;
    private string ParentId() => _sections.Count > 0 ? _sections[^1].Id : "n0";
    private string HeadingPath() => string.Join(" > ", new[] { _nodes[0].Title }.Concat(_sections.Select(s => s.Text)));
    private static string Clip(string s) => s.Length > MaxTitleChars ? s[..MaxTitleChars] : s;
    private static int Tokens(string s) => (s.Length + 3) / 4;

    // ---------------------------------------------------------------- splitting oversized text

    private List<string> Split(string text, bool byLines)
    {
        if (Tokens(text) <= maxChunkTokens) return [text];
        var parts = byLines ? text.Split('\n') : SentenceBreak().Split(text);
        return Pack(parts, byLines ? "\n" : " ", maxChunkTokens, part => Pack(part.Split(' ', StringSplitOptions.RemoveEmptyEntries), " ", maxChunkTokens, Cut));
    }

    // A single over-long word is cut into pieces that fit.
    private List<string> Cut(string word)
    {
        var size = maxChunkTokens * 4;
        var pieces = new List<string>();
        for (var i = 0; i < word.Length; i += size) pieces.Add(word.Substring(i, Math.Min(size, word.Length - i)));
        return pieces;
    }

    private static List<string> Pack(IEnumerable<string> parts, string separator, int limit, Func<string, List<string>> splitPart)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        var size = 0;

        void Close()
        {
            if (current.Length > 0) chunks.Add(current.ToString());
            current.Clear();
            size = 0;
        }

        foreach (var part in parts)
        {
            var tokens = Tokens(part);
            if (tokens > limit)
            {
                Close();
                chunks.AddRange(splitPart(part));
                continue;
            }
            if (size > 0 && size + tokens > limit) Close();
            if (current.Length > 0) current.Append(separator);
            current.Append(part);
            size += tokens;
        }
        Close();
        return chunks;
    }

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceBreak();
}
