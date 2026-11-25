using Knovara.Core.Contracts;
using Knovara.Core.Models;
using Knovara.Local.Storage;
using Microsoft.Data.Sqlite;

namespace Knovara.Local.Tests;

public sealed class SqliteKnowledgeStoreTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "knovara-local-" + Guid.NewGuid().ToString("n"));
    private SqliteDatabase _db = null!;
    private SqliteKnowledgeStore _store = null!;

    public async Task InitializeAsync()
    {
        _db = new SqliteDatabase(Path.Combine(_dir, "k.db"));
        await _db.InitializeAsync();
        _store = new SqliteKnowledgeStore(_db);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private static KnowledgeNode[] Nodes(string resourceId, string tag, params string[] passages)
    {
        var list = new List<KnowledgeNode> { new($"{tag}-root", resourceId, null, "document", "Doc " + tag, null, null, new Dictionary<string, string>()) };
        for (var i = 0; i < passages.Length; i++)
            list.Add(new($"{tag}-p{i}", resourceId, $"{tag}-root", "passage", "Section " + i, null, passages[i], new Dictionary<string, string>()));
        return list.ToArray();
    }

    private async Task<long> Count(string sql)
    {
        await using var c = await _db.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Register_is_idempotent_per_tenant_and_uri_and_isolated_between_tenants()
    {
        var a = await _store.RegisterResourceAsync("t1", "docs/a.md", SourceType.Document);
        var again = await _store.RegisterResourceAsync("t1", "docs/a.md", SourceType.Document);
        var other = await _store.RegisterResourceAsync("t2", "docs/a.md", SourceType.Document);

        Assert.Equal(a.ResourceId, again.ResourceId);
        Assert.NotEqual(a.ResourceId, other.ResourceId);
        Assert.Equal(ResourceStatus.Pending, a.Status);
        Assert.Null(await _store.GetResourceAsync("t2", a.ResourceId));
        Assert.NotNull(await _store.GetResourceAsync("t1", a.ResourceId));
    }

    [Fact]
    public async Task Commit_makes_resource_ready_and_replaces_passages_on_a_new_version()
    {
        var r = await _store.RegisterResourceAsync("t1", "a.md", SourceType.Document);
        Assert.Equal(VersionCheck.Changed, await _store.CheckVersionAsync("t1", r.ResourceId, "h1", "m"));

        var v1 = await _store.CommitVersionAsync("t1", r.ResourceId, "h1", Nodes(r.ResourceId, "a", "alpha text", "beta text"),
            new Dictionary<string, float[]> { ["a-p0"] = [1f, 0f], ["a-p1"] = [0f, 1f] }, "m");
        Assert.True(v1!.Applied);
        Assert.Equal(ResourceStatus.Ready, v1.Resource.Status);
        Assert.Equal("h1", v1.Resource.SourceVersion);
        Assert.Null(v1.SupersededVersion);

        Assert.Equal(VersionCheck.Unchanged, await _store.CheckVersionAsync("t1", r.ResourceId, "h1", "m"));

        var v2 = await _store.CommitVersionAsync("t1", r.ResourceId, "h2", Nodes(r.ResourceId, "b", "gamma text"),
            new Dictionary<string, float[]> { ["b-p0"] = [1f, 1f] }, "m");
        Assert.True(v2!.Applied);
        Assert.Equal("h1", v2.SupersededVersion);
        Assert.Equal(1, (await _store.ListPassagesAsync("t1", r.ResourceId, 10, 0))!.Total);
    }

    [Fact]
    public async Task Commit_of_an_unchanged_version_is_not_applied()
    {
        var r = await _store.RegisterResourceAsync("t1", "a.md", SourceType.Document);
        var emb = new Dictionary<string, float[]> { ["a-p0"] = [1f, 0f] };
        await _store.CommitVersionAsync("t1", r.ResourceId, "h1", Nodes(r.ResourceId, "a", "alpha"), emb, "m");
        var again = await _store.CommitVersionAsync("t1", r.ResourceId, "h1", Nodes(r.ResourceId, "z", "other"), emb, "m");

        Assert.False(again!.Applied);
        Assert.Equal("alpha", (await _store.GetCurrentPassagesAsync("t1", ["a-p0"]))[0].Content);
    }

    [Fact]
    public async Task A_different_embedding_model_makes_the_same_version_stale()
    {
        var r = await _store.RegisterResourceAsync("t1", "a.md", SourceType.Document);
        await _store.CommitVersionAsync("t1", r.ResourceId, "h1", Nodes(r.ResourceId, "a", "alpha"),
            new Dictionary<string, float[]> { ["a-p0"] = [1f] }, "old-model");

        Assert.Equal(VersionCheck.Changed, await _store.CheckVersionAsync("t1", r.ResourceId, "h1", "new-model"));
    }

    [Fact]
    public async Task Commit_for_another_tenants_resource_returns_null()
    {
        var r = await _store.RegisterResourceAsync("t1", "a.md", SourceType.Document);
        Assert.Null(await _store.CommitVersionAsync("t2", r.ResourceId, "h1", Nodes(r.ResourceId, "a", "x"), new Dictionary<string, float[]>(), "m"));
        Assert.Equal(VersionCheck.NotFound, await _store.CheckVersionAsync("t2", r.ResourceId, "h1", "m"));
    }

    [Fact]
    public async Task Passages_of_an_unfinished_resource_are_not_current()
    {
        var r = await _store.RegisterResourceAsync("t1", "a.md", SourceType.Document);
        Assert.Empty(await _store.GetCurrentPassagesAsync("t1", ["a-p0"]));
    }

    [Fact]
    public async Task Deleting_a_resource_removes_passages_uploads_and_full_text_entries()
    {
        var r = await _store.SaveUploadAsync("t1", "n.txt", "hello"u8.ToArray());
        await _store.CommitVersionAsync("t1", r.ResourceId, "h1", Nodes(r.ResourceId, "a", "findable words"), new Dictionary<string, float[]>(), "m");
        Assert.Equal(1, await Count("SELECT count(*) FROM knowledge_fts WHERE knowledge_fts MATCH 'findable'"));

        Assert.True(await _store.DeleteResourceAsync("t1", r.ResourceId));

        Assert.Equal(0, await Count("SELECT count(*) FROM knowledge_nodes"));
        Assert.Equal(0, await Count("SELECT count(*) FROM resource_uploads"));
        Assert.Equal(0, await Count("SELECT count(*) FROM knowledge_fts WHERE knowledge_fts MATCH 'findable'"));
        Assert.False(await _store.DeleteResourceAsync("t1", r.ResourceId));
    }

    [Fact]
    public async Task Bulk_delete_needs_a_selector_and_narrows_with_both()
    {
        var a = await _store.RegisterResourceAsync("t1", "a", SourceType.Document);
        var b = await _store.RegisterResourceAsync("t1", "b", SourceType.Document);
        await _store.MarkFailedAsync("t1", b.ResourceId, "boom");

        await Assert.ThrowsAsync<ArgumentException>(() => _store.DeleteResourcesAsync("t1", null, null));

        var none = await _store.DeleteResourcesAsync("t1", [a.ResourceId], ResourceStatus.Failed);
        Assert.Empty(none);

        var deleted = await _store.DeleteResourcesAsync("t1", null, ResourceStatus.Failed);
        Assert.Equal([b.ResourceId], deleted);
        Assert.NotNull(await _store.GetResourceAsync("t1", a.ResourceId));
    }

    [Fact]
    public async Task Query_filters_by_status_type_and_literal_uri_text()
    {
        await _store.RegisterResourceAsync("t1", "docs/100%_done.md", SourceType.Document);
        await _store.RegisterResourceAsync("t1", "docs/other.md", SourceType.Document);
        await _store.RegisterResourceAsync("t1", "https://x.test/page", SourceType.Website);

        var literal = await _store.QueryResourcesAsync("t1", new ResourceFilter(UriContains: "100%_done"));
        Assert.Equal(1, literal.Total);

        var docs = await _store.QueryResourcesAsync("t1", new ResourceFilter(Type: SourceType.Document, Status: ResourceStatus.Pending, Limit: 1));
        Assert.Equal(2, docs.Total);
        Assert.Single(docs.Items);
        Assert.Equal(0, (await _store.QueryResourcesAsync("t2", new ResourceFilter())).Total);
    }

    [Fact]
    public async Task Upload_round_trips_and_is_tenant_scoped()
    {
        var r = await _store.SaveUploadAsync("t1", "n.txt", [1, 2, 3]);
        Assert.Equal(UploadUri.For("n.txt"), r.Uri);
        var up = await _store.GetUploadAsync("t1", r.ResourceId);
        Assert.Equal("n.txt", up!.FileName);
        Assert.Equal(new byte[] { 1, 2, 3 }, up.Content);
        Assert.Null(await _store.GetUploadAsync("t2", r.ResourceId));

        var replaced = await _store.SaveUploadAsync("t1", "n.txt", [9]);
        Assert.Equal(r.ResourceId, replaced.ResourceId);
        Assert.Equal(new byte[] { 9 }, (await _store.GetUploadAsync("t1", r.ResourceId))!.Content);
    }

    [Fact]
    public async Task Stats_count_resources_passages_embeddings_and_bytes()
    {
        var r = await _store.SaveUploadAsync("t1", "n.txt", [1, 2, 3, 4]);
        await _store.CommitVersionAsync("t1", r.ResourceId, "h1", Nodes(r.ResourceId, "a", "abc", "de"),
            new Dictionary<string, float[]> { ["a-p0"] = [1f] }, "m");

        var s = await _store.GetStatsAsync("t1");
        Assert.Equal(1, s.Resources);
        Assert.Equal(2, s.Passages);
        Assert.Equal(1, s.EmbeddedPassages);
        Assert.Equal(5, s.ContentCharacters);
        Assert.Equal(4, s.UploadedBytes);
        Assert.Equal(1, s.ByStatus["Ready"]);
    }

    [Fact]
    public async Task MarkFailed_records_the_error()
    {
        var r = await _store.RegisterResourceAsync("t1", "a", SourceType.Document);
        Assert.True(await _store.MarkFailedAsync("t1", r.ResourceId, "bad"));
        var got = await _store.GetResourceAsync("t1", r.ResourceId);
        Assert.Equal(ResourceStatus.Failed, got!.Status);
        Assert.Equal("bad", got.Error);
    }
}
