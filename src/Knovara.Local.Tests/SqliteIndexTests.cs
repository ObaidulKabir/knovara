using Knovara.Core.Models;
using Knovara.Local.Storage;
using Microsoft.Data.Sqlite;

namespace Knovara.Local.Tests;

public sealed class SqliteIndexTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "knovara-local-" + Guid.NewGuid().ToString("n"));
    private SqliteKnowledgeStore _store = null!;
    private SqliteIndexManagementService _index = null!;

    public async Task InitializeAsync()
    {
        var db = new SqliteDatabase(Path.Combine(_dir, "k.db"));
        await db.InitializeAsync();
        _store = new SqliteKnowledgeStore(db);
        _index = new SqliteIndexManagementService(db);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private async Task Seed(string tenant, string uri, string tag, params (string title, string text, float[] vec)[] passages)
    {
        var r = await _store.RegisterResourceAsync(tenant, uri, SourceType.Document);
        var nodes = new List<KnowledgeNode> { new($"{tag}-root", r.ResourceId, null, "document", tag, null, null, new Dictionary<string, string>()) };
        var emb = new Dictionary<string, float[]>();
        for (var i = 0; i < passages.Length; i++)
        {
            nodes.Add(new($"{tag}-p{i}", r.ResourceId, $"{tag}-root", "passage", passages[i].title, null, passages[i].text, new Dictionary<string, string>()));
            emb[$"{tag}-p{i}"] = passages[i].vec;
        }
        await _store.CommitVersionAsync(tenant, r.ResourceId, "v1", nodes, emb, "m");
    }

    [Theory]
    [InlineData("How do I rotate the signing key?", "\"rotate\" OR \"signing\" OR \"key\"")]
    [InlineData("what is the 'refund' policy; DROP TABLE x --", "\"refund\" OR \"policy\" OR \"drop\" OR \"table\" OR \"x\"")]
    [InlineData("the of and", null)]
    public void Match_expression_keeps_words_drops_stop_words_and_syntax(string query, string? expected) =>
        Assert.Equal(expected, SqliteIndexManagementService.BuildMatchExpression(query));

    [Fact]
    public async Task Lexical_finds_stemmed_terms_and_ranks_by_relevance()
    {
        await Seed("t1", "a", "a",
            ("Billing", "Invoices are issued monthly and refunds are processed within five days.", [1, 0]),
            ("Other", "Completely unrelated gardening notes about tomatoes.", [0, 1]));

        var hits = await _index.SearchLexicalAsync("how are refunded invoices handled?", "t1", 5);

        Assert.Single(hits);
        Assert.Equal("a-p0", hits[0].NodeId);
        Assert.Equal(IndexKind.Lexical, hits[0].RetrievedVia);
        Assert.True(hits[0].RelevanceScore > 0);
    }

    [Fact]
    public async Task Lexical_is_tenant_scoped_and_ignores_unfinished_resources()
    {
        await Seed("t1", "a", "a", ("T", "needle in a haystack", [1, 0]));
        await _store.RegisterResourceAsync("t1", "pending.md", SourceType.Document);

        Assert.Single(await _index.SearchLexicalAsync("needle", "t1", 5));
        Assert.Empty(await _index.SearchLexicalAsync("needle", "t2", 5));
    }

    [Fact]
    public async Task Lexical_never_returns_deleted_or_superseded_text()
    {
        await Seed("t1", "a", "a", ("T", "obsolete wording", [1, 0]));
        var r = (await _store.QueryResourcesAsync("t1", new ResourceFilter())).Items[0];
        await _store.CommitVersionAsync("t1", r.ResourceId, "v2",
            [new("b-p0", r.ResourceId, null, "passage", "T", null, "fresh wording", new Dictionary<string, string>())],
            new Dictionary<string, float[]> { ["b-p0"] = [1f, 0f] }, "m");

        Assert.Empty(await _index.SearchLexicalAsync("obsolete", "t1", 5));
        Assert.Single(await _index.SearchLexicalAsync("fresh", "t1", 5));

        await _store.DeleteResourceAsync("t1", r.ResourceId);
        Assert.Empty(await _index.SearchLexicalAsync("fresh", "t1", 5));
    }

    [Fact]
    public async Task Semantic_returns_nearest_by_cosine_best_first_limited_to_topK()
    {
        await Seed("t1", "a", "a",
            ("one", "first", [1f, 0f]),
            ("two", "second", [0.8f, 0.6f]),
            ("three", "third", [0f, 1f]));

        var hits = await _index.SearchSemanticAsync([1f, 0f], "m", "t1", 2);

        Assert.Equal(["a-p0", "a-p1"], hits.Select(h => h.NodeId));
        Assert.Equal(1.0, hits[0].RelevanceScore, 4);
        Assert.Equal(0.8, hits[1].RelevanceScore, 4);
        Assert.All(hits, h => Assert.Equal(IndexKind.Semantic, h.RetrievedVia));
    }

    [Fact]
    public async Task Semantic_only_considers_the_requested_model_and_tenant()
    {
        await Seed("t1", "a", "a", ("one", "first", [1f, 0f]));

        Assert.Empty(await _index.SearchSemanticAsync([1f, 0f], "other-model", "t1", 5));
        Assert.Empty(await _index.SearchSemanticAsync([1f, 0f], "m", "t2", 5));
        Assert.Empty(await _index.SearchSemanticAsync([1f, 0f, 0f], "m", "t1", 5)); // different dimension is skipped, not an error
    }
}
