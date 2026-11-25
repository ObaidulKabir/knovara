using Knovara.Core.Models;
using Knovara.Local.Ingestion;

namespace Knovara.Local.Tests;

public sealed class ExtractorTests
{
    private const string Doc = """
        ---
        title: ignored front matter
        ---
        # Payment Service Guide

        Intro paragraph about the service.

        ## Retries

        The service retries a failed charge up to five times. Set `max_attempts_per_hour` to change it.

        - first item
        - second item

        ### Backoff

        ```
        retry_after = 30
        ```

        | Code | Meaning |
        |---|---|
        | 503 | retry |
        """;

    [Fact]
    public void Markdown_builds_title_sections_and_passages_with_heading_paths()
    {
        var nodes = MarkdownExtractor.Extract(Doc, "guide.md");

        Assert.Equal("document", nodes[0].Level);
        Assert.Equal("Payment Service Guide", nodes[0].Title);   // the opening H1 is the title, not a section
        Assert.Null(nodes[0].ParentLocalId);

        var sections = nodes.Where(n => n.Level == "section").ToList();
        Assert.Equal(["Payment Service Guide > Retries", "Payment Service Guide > Retries > Backoff"], sections.Select(s => s.Title));
        Assert.Equal(sections[0].LocalId, sections[1].ParentLocalId);

        var retry = nodes.Single(n => n.Content?.Contains("up to five times") == true);
        Assert.Equal("Payment Service Guide > Retries", retry.Title);
        Assert.Contains("max_attempts_per_hour", retry.Content);   // identifiers survive inline clean-up
        Assert.DoesNotContain("`", retry.Content);
    }

    [Fact]
    public void Markdown_keeps_code_and_tables_as_their_own_passages_and_joins_small_neighbours()
    {
        var nodes = MarkdownExtractor.Extract(Doc, "guide.md");

        Assert.Contains(nodes, n => n.Level == "code" && n.Content!.Contains("retry_after = 30"));
        Assert.Contains(nodes, n => n.Level == "table" && n.Content!.Contains("| 503 | retry |"));

        // "The service retries..." and the two list items are each tiny, so they join into one passage.
        var merged = nodes.Single(n => n.Content?.Contains("up to five times") == true);
        Assert.Contains("first item", merged.Content);
        Assert.Equal("text", merged.Level);
    }

    [Fact]
    public void Large_paragraphs_are_split_on_sentence_boundaries_under_the_size_cap()
    {
        var sentence = "This sentence is exactly long enough to count for something when repeated many times over. ";
        var nodes = MarkdownExtractor.Extract("# T\n\n## S\n\n" + string.Concat(Enumerable.Repeat(sentence, 80)), "big.md");

        var passages = nodes.Where(n => n.Content is not null).ToList();
        Assert.True(passages.Count > 1);
        Assert.All(passages, p => Assert.True(p.Content!.Length <= 384 * 4 + 100, $"{p.Content.Length} chars"));
        Assert.All(passages, p => Assert.EndsWith(".", p.Content!.TrimEnd()));
    }

    [Fact]
    public void Html_extracts_headings_text_lists_and_tables_and_skips_chrome()
    {
        const string html = """
            <html><head><title>Token Refresh</title><style>.x{}</style></head>
            <body><nav>Home | About</nav>
            <h2>Lifetime</h2><p>Access tokens last <b>15</b> minutes.</p>
            <ul><li>refresh early</li><li>never log tokens</li></ul>
            <pre>POST /refresh</pre>
            <table><tr><th>Code</th><th>Meaning</th></tr><tr><td>401</td><td>expired</td></tr></table>
            <script>alert(1)</script></body></html>
            """;

        var nodes = HtmlExtractor.Extract(html, "token-refresh.html");

        Assert.Equal("Token Refresh", nodes[0].Title);
        Assert.Contains(nodes, n => n.Level == "section" && n.Title == "Token Refresh > Lifetime");
        Assert.Contains(nodes, n => n.Content?.Contains("Access tokens last 15 minutes.") == true);
        Assert.Contains(nodes, n => n.Level == "code" && n.Content == "POST /refresh");
        Assert.Contains(nodes, n => n.Level == "table" && n.Content!.Contains("| 401 | expired |"));
        Assert.DoesNotContain(nodes, n => (n.Content ?? "").Contains("Home | About") || (n.Content ?? "").Contains("alert"));
    }

    [Fact]
    public void Empty_documents_still_produce_a_root()
    {
        var nodes = MarkdownExtractor.Extract("", "empty.md");
        Assert.Single(nodes);
        Assert.Equal("empty.md", nodes[0].Title);
    }
}
