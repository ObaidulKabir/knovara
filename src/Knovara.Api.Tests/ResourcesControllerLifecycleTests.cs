using System.Text;
using Knovara.Api.Controllers;
using Knovara.Core.Contracts;
using Knovara.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Knovara.Api.Tests;

/// <summary>
/// The tools an external system needs to manage its own document volume: filtered/paged listing, stats,
/// inspecting extracted passages, uploading content, re-ingesting, and deleting singly or in bulk.
/// </summary>
public class ResourcesControllerLifecycleTests
{
    private readonly Mock<IKnowledgeStore> _store = new();
    private readonly Mock<IIngestionQueue> _queue = new();

    private ResourcesController CreateController()
    {
        var controller = new ResourcesController(
            _store.Object, _queue.Object, Mock.Of<IKnowledgeRepresentationService>(), Mock.Of<IContextMemoryService>(), Mock.Of<IModelService>());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static ResourceReference Resource(string id = "r1", string uri = "upload://a.md", ResourceStatus status = ResourceStatus.Pending)
        => new(id, SourceType.Document, uri, null, "acme", status);

    // ---- listing -------------------------------------------------------------------------------

    [Fact]
    public async Task List_PassesFiltersAndPagingToTheStore_AndReportsTotalInHeader()
    {
        ResourceFilter? seen = null;
        _store.Setup(s => s.QueryResourcesAsync("acme", It.IsAny<ResourceFilter>(), It.IsAny<CancellationToken>()))
              .Callback<string, ResourceFilter, CancellationToken>((_, f, _) => seen = f)
              .ReturnsAsync(new ResourcePage([Resource()], Total: 42));
        var controller = CreateController();

        var result = await controller.List("acme", ResourceStatus.Failed, SourceType.Website, "arxiv", CancellationToken.None, limit: 10, offset: 20);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(new ResourceFilter(ResourceStatus.Failed, SourceType.Website, "arxiv", 10, 20), seen);
        Assert.Equal("42", controller.Response.Headers["X-Total-Count"].ToString());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(501, 0)]
    [InlineData(10, -1)]
    public async Task List_RejectsOutOfRangePaging(int limit, int offset)
    {
        var result = await CreateController().List("acme", null, null, null, CancellationToken.None, limit, offset);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _store.Verify(s => s.QueryResourcesAsync(It.IsAny<string>(), It.IsAny<ResourceFilter>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- stats / passages ----------------------------------------------------------------------

    [Fact]
    public async Task Stats_ReturnsTheStoresNumbersForTheCallersTenant()
    {
        var stats = new ResourceStats(3, new Dictionary<string, int> { ["Ready"] = 2, ["Failed"] = 1 }, new Dictionary<string, int>(), 120, 120, 50_000, 1_000);
        _store.Setup(s => s.GetStatsAsync("acme", It.IsAny<CancellationToken>())).ReturnsAsync(stats);

        var result = await CreateController().Stats("acme", CancellationToken.None);

        Assert.Same(stats, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task Passages_ForMissingResource_IsNotFound()
    {
        _store.Setup(s => s.ListPassagesAsync("acme", "nope", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync((PassagePage?)null);

        Assert.IsType<NotFoundResult>((await CreateController().Passages("acme", "nope", CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Passages_RejectsOversizedPage()
    {
        var result = await CreateController().Passages("acme", "r1", CancellationToken.None, limit: 1000);
        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    // ---- upload --------------------------------------------------------------------------------

    [Fact]
    public async Task Upload_TextContent_StoresUtf8BytesAndQueuesTheJob()
    {
        byte[]? stored = null;
        _store.Setup(s => s.SaveUploadAsync("acme", "notes.md", It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
              .Callback<string, string, byte[], CancellationToken>((_, _, bytes, _) => stored = bytes)
              .ReturnsAsync(Resource(uri: "upload://notes.md"));

        var result = await CreateController().Upload("acme", new UploadRequest("notes.md", Content: "# Hi é"), CancellationToken.None);

        Assert.IsType<AcceptedAtActionResult>(result.Result);
        Assert.Equal("# Hi é", Encoding.UTF8.GetString(stored!));
        _queue.Verify(q => q.EnqueueAsync(It.Is<IngestionJob>(j => j.TenantId == "acme" && j.Uri == "upload://notes.md"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Upload_Base64_DecodesTheBytes()
    {
        byte[]? stored = null;
        _store.Setup(s => s.SaveUploadAsync("acme", "doc.pdf", It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
              .Callback<string, string, byte[], CancellationToken>((_, _, bytes, _) => stored = bytes)
              .ReturnsAsync(Resource(uri: "upload://doc.pdf"));

        await CreateController().Upload("acme", new UploadRequest("doc.pdf", ContentBase64: Convert.ToBase64String([1, 2, 3, 255])), CancellationToken.None);

        Assert.Equal(new byte[] { 1, 2, 3, 255 }, stored);
    }

    [Theory]
    [InlineData("a.exe", "x", null)]         // unsupported format
    [InlineData(".md", "x", null)]           // no name
    [InlineData("a.md", null, null)]         // no content at all
    [InlineData("a.md", "x", "eA==")]        // both content kinds
    [InlineData("a.md", "", null)]           // empty
    [InlineData("a.pdf", null, "not base64!")]
    public async Task Upload_RejectsBadRequests_WithoutTouchingTheStoreOrQueue(string fileName, string? content, string? base64)
    {
        var result = await CreateController().Upload("acme", new UploadRequest(fileName, content, base64), CancellationToken.None);

        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _store.Verify(s => s.SaveUploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<IngestionJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Upload_TooLarge_Is413()
    {
        var big = Convert.ToBase64String(new byte[26 * 1024 * 1024]);

        var result = await CreateController().Upload("acme", new UploadRequest("big.pdf", ContentBase64: big), CancellationToken.None);

        Assert.Equal(413, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    [Theory]
    [InlineData("notes.md", "notes.md")]
    [InlineData("NOTES.TXT", "NOTES.md")]                  // plain text is read as markdown
    [InlineData("C:\\docs\\plan.docx", "plan.docx")]       // directories are stripped, never trusted
    [InlineData("../../etc/secret.pdf", "secret.pdf")]
    [InlineData("a/b/page.HTML", "page.HTML")]
    public void NormalizeUploadName_KeepsOnlyASafeFileName(string input, string expected)
        => Assert.Equal(expected, ResourcesController.NormalizeUploadName(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("script.sh")]
    [InlineData(".md")]
    [InlineData("a\u0000b.md")]
    public void NormalizeUploadName_RefusesUnsafeOrUnsupportedNames(string input)
        => Assert.Null(ResourcesController.NormalizeUploadName(input));

    // ---- content / reingest --------------------------------------------------------------------

    [Fact]
    public async Task Content_ReturnsTheUploadedBytes_OrNotFoundForUrlSources()
    {
        _store.Setup(s => s.GetUploadAsync("acme", "r1", It.IsAny<CancellationToken>())).ReturnsAsync(new StoredUpload("a.md", [7, 8]));
        _store.Setup(s => s.GetUploadAsync("acme", "r2", It.IsAny<CancellationToken>())).ReturnsAsync((StoredUpload?)null);

        var file = Assert.IsType<FileContentResult>(await CreateController().Content("acme", "r1", CancellationToken.None));
        Assert.Equal(new byte[] { 7, 8 }, file.FileContents);
        Assert.Equal("a.md", file.FileDownloadName);
        Assert.IsType<NotFoundResult>(await CreateController().Content("acme", "r2", CancellationToken.None));
    }

    [Fact]
    public async Task Reingest_RequeuesTheSameUri_AndIsNotFoundForUnknownIds()
    {
        var existing = Resource("r1", "https://example.com/p", ResourceStatus.Failed) with { Type = SourceType.Website };
        _store.Setup(s => s.GetResourceAsync("acme", "r1", It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _store.Setup(s => s.GetResourceAsync("acme", "nope", It.IsAny<CancellationToken>())).ReturnsAsync((ResourceReference?)null);
        _store.Setup(s => s.RegisterResourceAsync("acme", "https://example.com/p", SourceType.Website, It.IsAny<CancellationToken>()))
              .ReturnsAsync(existing with { Status = ResourceStatus.Pending });

        Assert.IsType<AcceptedAtActionResult>((await CreateController().Reingest("acme", "r1", CancellationToken.None)).Result);
        _queue.Verify(q => q.EnqueueAsync(It.Is<IngestionJob>(j => j.ResourceId == "r1" && j.Uri == "https://example.com/p"), It.IsAny<CancellationToken>()), Times.Once);

        Assert.IsType<NotFoundResult>((await CreateController().Reingest("acme", "nope", CancellationToken.None)).Result);
    }

    // ---- bulk delete ---------------------------------------------------------------------------

    [Fact]
    public async Task BulkDelete_ByIds_ReportsWhatWasAndWasNotDeleted()
    {
        _store.Setup(s => s.DeleteResourcesAsync("acme", It.IsAny<IReadOnlyCollection<string>>(), null, It.IsAny<CancellationToken>()))
              .ReturnsAsync(["a", "b"]);

        var result = await CreateController().BulkDelete("acme", new BulkDeleteRequest(["a", "b", "ghost", "a"]), CancellationToken.None);

        var body = Assert.IsType<BulkDeleteResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(["a", "b"], body.Deleted);
        Assert.Equal(["ghost"], body.NotDeleted);
    }

    [Fact]
    public async Task BulkDelete_ByStatusAlone_IsAllowed()
    {
        _store.Setup(s => s.DeleteResourcesAsync("acme", null, ResourceStatus.Failed, It.IsAny<CancellationToken>())).ReturnsAsync(["x"]);

        var result = await CreateController().BulkDelete("acme", new BulkDeleteRequest(Status: ResourceStatus.Failed), CancellationToken.None);

        Assert.Equal(["x"], Assert.IsType<BulkDeleteResponse>(Assert.IsType<OkObjectResult>(result.Result).Value).Deleted);
    }

    [Fact]
    public async Task BulkDelete_WithNoSelector_IsRefused_SoItCanNeverMeanEverything()
    {
        foreach (var request in new[] { new BulkDeleteRequest(), new BulkDeleteRequest([]), new BulkDeleteRequest(["  "]) })
        {
            var result = await CreateController().BulkDelete("acme", request, CancellationToken.None);
            Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        }
        _store.Verify(s => s.DeleteResourcesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<ResourceStatus?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BulkDelete_TooManyIds_IsRefused()
    {
        var ids = Enumerable.Range(0, 1001).Select(i => $"r{i}").ToList();
        var result = await CreateController().BulkDelete("acme", new BulkDeleteRequest(ids), CancellationToken.None);
        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    // ---- ingest ---------------------------------------------------------------------------------

    [Fact]
    public async Task Ingest_Code_IsRejected_SoClientsMustNotOfferIt()
    {
        var result = await CreateController().Ingest("acme", new IngestRequest("https://x/y.cs", SourceType.Code), CancellationToken.None);
        Assert.Equal(400, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }
}
