using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Knovara.Core.Models;

namespace Knovara.Local.Ingestion;

/// <summary>HTML to the knowledge hierarchy: headings become sections; paragraphs, list items, code and tables become passages.</summary>
internal static class HtmlExtractor
{
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "nav", "header", "footer", "aside", "form", "svg", "iframe", "head"
    };

    private static readonly HashSet<string> ParagraphTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "blockquote", "dd", "dt", "figcaption", "address", "summary"
    };

    public static IReadOnlyList<ExtractedNode> Extract(string html, string sourceName)
    {
        var document = new HtmlParser().ParseDocument(html);
        var title = Clean(document.Title ?? "");

        var blocks = new List<Block>();
        Visit(document.Body ?? (INode)document.DocumentElement, blocks);

        // The page <title> names the document unless the body opens with its own level-1 heading.
        var opensWithH1 = blocks.FirstOrDefault() is HeadingBlock { Level: 1 };
        return new HierarchyAssembler(sourceName).Build(blocks, opensWithH1 || title.Length == 0 ? null : title);
    }

    private static void Visit(INode node, List<Block> blocks)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is not IElement el) continue;
            var tag = el.LocalName;
            if (Skipped.Contains(tag)) continue;

            if (tag.Length == 2 && tag[0] == 'h' && char.IsDigit(tag[1]) && tag[1] is >= '1' and <= '6')
            {
                blocks.Add(new HeadingBlock(tag[1] - '0', Clean(el.TextContent)));
            }
            else if (ParagraphTags.Contains(tag))
            {
                blocks.Add(new TextBlock("paragraph", Clean(el.TextContent)));
            }
            else if (tag == "pre")
            {
                blocks.Add(new TextBlock("code", el.TextContent.Trim('\n'), Standalone: true));
            }
            else if (tag == "table")
            {
                blocks.Add(new TextBlock("table", TableText(el), Standalone: true));
            }
            else if (tag == "li")
            {
                // The item's own text; nested lists are visited as items of their own.
                var own = string.Concat(el.ChildNodes.Where(n => !(n is IElement e && (e.LocalName is "ul" or "ol"))).Select(n => n.TextContent));
                blocks.Add(new TextBlock("list_item", Clean(own), ListKey: ListKey(el)));
                foreach (var nested in el.Children.Where(e => e.LocalName is "ul" or "ol")) Visit(nested, blocks);
            }
            else
            {
                Visit(el, blocks);
            }
        }
    }

    private static string ListKey(IElement item) => "L" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item.ParentElement);

    /// <summary>Rows as pipe-separated cells, like the markdown tables the Docling worker produces.</summary>
    private static string TableText(IElement table) => string.Join("\n",
        table.QuerySelectorAll("tr").Select(row => "| " + string.Join(" | ", row.Children.Where(c => c.LocalName is "td" or "th").Select(c => Clean(c.TextContent))) + " |"));

    private static string Clean(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
