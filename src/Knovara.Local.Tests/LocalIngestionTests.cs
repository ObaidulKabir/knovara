using Knovara.ContextMemory;
using Knovara.Core.Contracts;
using Knovara.Core.Ingestion;
using Knovara.Core.Models;
using Knovara.KnowledgeRepresentation;
using Knovara.Local.Ingestion;
using Knovara.Local.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Knovara.Local.Tests;

/// <summary>Upload -> in-process worker -> SQLite store -> search, with a deterministic stand-in for the ONNX models.</summary>
public sealed class LocalIngestionTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "knovara-local-" + Guid.NewGuid().ToString("n"));
    private SqliteKnowledgeStore _store = null!;
    private SqliteIndexManagementService _index = null!;
    private LocalIngestionWorker _worker = null!;

    /// <summary>Bag-of-letters vectors: texts sharing words are close, which is all these tests need from an embedder.</summary>
    private sealed class FakeModels : IModelService
    {
        public Task<ModelInfo> GetInfoAsync(CancellationToken ct = default) => Task.FromResult(new ModelInfo("fake", 26, "fake-rerank"));
        public Task<IReadOnlyList<float[]>> EmbedPassagesAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Vector).ToList());
        public Task<float[]> EmbedQueryAsync(string question, CancellationToken ct = default) => Task.FromResult(Vector(question));
        public Task<IReadOnlyList<double>> RerankAsync(string question, IReadOnlyList<string> documents, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<double>>(documents.Select(_ => 0.0).ToList());

        private static float[] Vector(string text)
        {
            var v = new float[26];
            foreach (var c in text.ToLowerInvariant().Where(ch => ch is >= 'a' and <= 'z')) v[c - 'a']++;
            return v;
        }
    }

    public async Task InitializeAsync()
    {
        var db = new SqliteDatabase(Path.Combine(_dir, "k.db"));
        await db.InitializeAsync();
        _store = new SqliteKnowledgeStore(db);
        _index = new SqliteIndexManagementService(db);

        var models = new FakeModels();
        var committer = new ExtractionCommitter(_store, new HierarchyBuilder(), new InMemoryContextMemoryService(), models);
        var options = new LocalOptions { SourceRoot = Path.Combine(_dir, "sources") };
        Directory.CreateDirectory(options.SourceRoot);
        _worker = new LocalIngestionWorker(new InProcessIngestionQueue(), _store, models, committer,
            new SourceFetcher(_store, options, new HttpClient()), NullLogger<LocalIngestionWorker>.Instance);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private async Task<ResourceReference> Upload(string name, string text, string tenant = "t1")
    {
        var r = await _store.SaveUploadAsync(tenant, name, System.Text.Encoding.UTF8.GetBytes(text));
        await _worker.ProcessAsync(new IngestionJob(r.ResourceId, tenant, r.Uri, r.Type), CancellationToken.None);
        return (await _store.GetResourceAsync(tenant, r.ResourceId))!;
    }

    [Fact]
    public async Task An_uploaded_markdown_document_becomes_ready_and_searchable()
    {
        var r = await Upload("guide.md", "# Guide\n\n## Retries\n\nThe service retries a failed charge up to five times.");

        Assert.Equal(ResourceStatus.Ready, r.Status);
        Assert.StartsWith("sha256:", r.SourceVersion);

        var lexical = await _index.SearchLexicalAsync("how many retries for a failed charge", "t1", 3);
        Assert.Contains("five times", lexical[0].Content);

        var semantic = await _index.SearchSemanticAsync((await new FakeModels().EmbedQueryAsync("retries failed charge")), "fake", "t1", 3);
        Assert.NotEmpty(semantic);
    }

    [Fact]
    public async Task Reingesting_unchanged_content_is_skipped_and_changed_content_replaces_the_old_passages()
    {
        var first = await Upload("g.md", "# G\n\nThe limit is five.");
        var again = await Upload("g.md", "# G\n\nThe limit is five.");
        Assert.Equal(first.SourceVersion, again.SourceVersion);

        var changed = await Upload("g.md", "# G\n\nThe limit is ten.");
        Assert.NotEqual(first.SourceVersion, changed.SourceVersion);
        Assert.Empty(await _index.SearchLexicalAsync("five", "t1", 3));
        Assert.Single(await _index.SearchLexicalAsync("ten", "t1", 3));
    }

    [Fact]
    public async Task Html_uploads_work_too()
    {
        var r = await Upload("page.html", "<html><head><title>T</title></head><body><h2>Refresh</h2><p>Renew tokens five minutes early.</p></body></html>");
        Assert.Equal(ResourceStatus.Ready, r.Status);
        Assert.Single(await _index.SearchLexicalAsync("renew tokens", "t1", 3));
    }

    [Fact]
    public async Task Unsupported_formats_fail_with_a_message_that_says_what_to_do()
    {
        var r = await Upload("report.pdf", "%PDF-1.4 not really");
        Assert.Equal(ResourceStatus.Failed, r.Status);
        Assert.Contains("Docling", r.Error);
    }

    [Fact]
    public async Task Relative_paths_resolve_only_inside_the_source_folder()
    {
        File.WriteAllText(Path.Combine(_dir, "sources", "note.md"), "# Note\n\nPlain facts about whales.");
        File.WriteAllText(Path.Combine(_dir, "secret.md"), "# Secret\n\nnot to be read");

        var inside = await _store.RegisterResourceAsync("t1", "note.md", SourceType.Document);
        await _worker.ProcessAsync(new IngestionJob(inside.ResourceId, "t1", inside.Uri, inside.Type), CancellationToken.None);
        Assert.Equal(ResourceStatus.Ready, (await _store.GetResourceAsync("t1", inside.ResourceId))!.Status);

        var outside = await _store.RegisterResourceAsync("t1", "../secret.md", SourceType.Document);
        await _worker.ProcessAsync(new IngestionJob(outside.ResourceId, "t1", outside.Uri, outside.Type), CancellationToken.None);
        var failed = (await _store.GetResourceAsync("t1", outside.ResourceId))!;
        Assert.Equal(ResourceStatus.Failed, failed.Status);
        Assert.Contains("outside the source folder", failed.Error);
        Assert.Empty(await _index.SearchLexicalAsync("secret", "t1", 3));
    }

    [Fact]
    public async Task A_resource_deleted_while_queued_is_ignored()
    {
        var r = await _store.SaveUploadAsync("t1", "gone.md", "# x\n\ny"u8.ToArray());
        await _store.DeleteResourceAsync("t1", r.ResourceId);

        // The upload bytes went with the resource, so the fetch fails; the worker must not throw.
        await _worker.ProcessAsync(new IngestionJob(r.ResourceId, "t1", r.Uri, r.Type), CancellationToken.None);
        Assert.Null(await _store.GetResourceAsync("t1", r.ResourceId));
    }

    [Theory]
    [InlineData("93.184.216.34", true)]
    [InlineData("10.1.2.3", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("192.168.0.5", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    public void Only_public_addresses_may_be_fetched(string ip, bool expected) =>
        Assert.Equal(expected, SourceFetcher.IsGlobal(System.Net.IPAddress.Parse(ip)));
}
