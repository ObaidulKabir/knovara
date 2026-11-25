using System.Text;
using System.Text.RegularExpressions;
using Knovara.Core.Models;

namespace Knovara.Local.Ingestion;

/// <summary>Markdown (and plain text, which is valid markdown) to the knowledge hierarchy.</summary>
internal static partial class MarkdownExtractor
{
    public static IReadOnlyList<ExtractedNode> Extract(string markdown, string sourceName) =>
        new HierarchyAssembler(sourceName).Build(ReadBlocks(StripFrontMatter(markdown.Replace("\r\n", "\n").Replace('\r', '\n'))));

    private static string StripFrontMatter(string text)
    {
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) return text;
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        return end < 0 ? text : text[(end + 4)..].TrimStart('\n');
    }

    private static IEnumerable<Block> ReadBlocks(string text)
    {
        var lines = text.Split('\n');
        var paragraph = new StringBuilder();
        var listCounter = 0;
        string? currentList = null;
        TextBlock? listItem = null;

        Block? FlushParagraph()
        {
            if (paragraph.Length == 0) return null;
            var block = new TextBlock("paragraph", paragraph.ToString().Trim());
            paragraph.Clear();
            return block;
        }

        TextBlock? FlushListItem()
        {
            var item = listItem;
            listItem = null;
            return item;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();

            var fence = FenceStart().Match(line);
            if (fence.Success)
            {
                if (FlushParagraph() is { } p0) yield return p0;
                if (FlushListItem() is { } l0) yield return l0;
                currentList = null;

                var code = new StringBuilder();
                var marker = fence.Groups[1].Value[..3];
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith(marker, StringComparison.Ordinal); i++) code.AppendLine(lines[i]);
                yield return new TextBlock("code", code.ToString(), Standalone: true);
                continue;
            }

            var heading = Heading().Match(line);
            if (heading.Success)
            {
                if (FlushParagraph() is { } p1) yield return p1;
                if (FlushListItem() is { } l1) yield return l1;
                currentList = null;
                yield return new HeadingBlock(heading.Groups[1].Length, StripInline(heading.Groups[2].Value));
                continue;
            }

            if (line.Length == 0)
            {
                if (FlushParagraph() is { } p2) yield return p2;
                if (FlushListItem() is { } l2) yield return l2;
                // A blank line ends the paragraph; a list may continue after it, so currentList stays.
                continue;
            }

            if (line.TrimStart().StartsWith('|'))
            {
                if (FlushParagraph() is { } p3) yield return p3;
                if (FlushListItem() is { } l3) yield return l3;
                currentList = null;

                var table = new StringBuilder();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('|'); i++) table.AppendLine(lines[i].TrimEnd());
                i--;
                yield return new TextBlock("table", table.ToString(), Standalone: true);
                continue;
            }

            var item = ListItem().Match(line);
            if (item.Success)
            {
                if (FlushParagraph() is { } p4) yield return p4;
                if (FlushListItem() is { } l4) yield return l4;
                currentList ??= "L" + ++listCounter;
                listItem = new TextBlock("list_item", StripInline(item.Groups[2].Value), ListKey: currentList);
                continue;
            }

            if (listItem is not null && line.StartsWith("  ", StringComparison.Ordinal))
            {
                // An indented continuation line belongs to the list item above it.
                listItem = listItem with { Text = listItem.Text + " " + StripInline(line.Trim()) };
                continue;
            }

            if (FlushListItem() is { } l5) yield return l5;
            currentList = null;
            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(StripInline(line.Trim()));
        }

        if (FlushParagraph() is { } pEnd) yield return pEnd;
        if (FlushListItem() is { } lEnd) yield return lEnd;
    }

    /// <summary>Drops emphasis markers and turns links and images into their visible text, so passages read as prose.</summary>
    private static string StripInline(string text)
    {
        text = Image().Replace(text, "$1");
        text = Link().Replace(text, "$1");
        text = Emphasis().Replace(text, "$2");
        text = UnderscoreEmphasis().Replace(text, "$2");
        return text.Replace("`", "");
    }

    [GeneratedRegex(@"^\s{0,3}(```+|~~~+)")]
    private static partial Regex FenceStart();

    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s*([-*+]|\d+[.)])\s+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"(\*\*|\*)(?=\S)(.+?)(?<=\S)\1")]
    private static partial Regex Emphasis();

    // Underscore emphasis only at word edges, so identifiers such as max_attempts_per_hour are left alone.
    [GeneratedRegex(@"(?<!\w)(__|_)(?=\S)(.+?)(?<=\S)\1(?!\w)")]
    private static partial Regex UnderscoreEmphasis();
}
