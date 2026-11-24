using Knovara.Api.Controllers;
using Knovara.Core.Contracts;
using Knovara.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Knovara.Api.Tests;

/// <summary>Covers the document-management endpoints: listing and deleting a tenant's resources.</summary>
public class ResourcesControllerManagementTests
{
    private readonly Mock<IKnowledgeStore> _mockStore = new();
    private readonly Mock<IIngestionQueue> _mockQueue = new();
    private readonly Mock<IKnowledgeRepresentationService> _mockKnowledge = new();
    private readonly Mock<IContextMemoryService> _mockMemory = new();
    private readonly Mock<IModelService> _mockModels = new();

    private ResourcesController CreateController() =>
        new(_mockStore.Object, _mockQueue.Object, _mockKnowledge.Object, _mockMemory.Object, _mockModels.Object);

    [Fact]
    public async Task List_ReturnsOnlyWhatTheStoreGivesForThisTenant()
    {
        var resources = new List<ResourceReference>
        {
            new("r1", SourceType.Document, "samples/a.md", "sha256:aaa", "acme", ResourceStatus.Ready),
            new("r2", SourceType.Website, "https://example.com", null, "acme", ResourceStatus.Pending)
        };
        _mockStore.Setup(s => s.ListResourcesAsync("acme", It.IsAny<CancellationToken>())).ReturnsAsync(resources);

        var result = await CreateController().List("acme", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var returned = Assert.IsAssignableFrom<IReadOnlyList<ResourceReference>>(ok.Value);
        Assert.Equal(2, returned.Count);
        _mockStore.Verify(s => s.ListResourcesAsync("acme", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task List_PassesTheCallersTenant_NeverAnotherOne()
    {
        _mockStore.Setup(s => s.ListResourcesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new List<ResourceReference>());

        await CreateController().List("tenant-from-header", CancellationToken.None);

        _mockStore.Verify(s => s.ListResourcesAsync("tenant-from-header", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_ExistingResource_ReturnsNoContent()
    {
        _mockStore.Setup(s => s.DeleteResourceAsync("acme", "r1", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await CreateController().Delete("acme", "r1", CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task Delete_UnknownResource_ReturnsNotFound()
    {
        _mockStore.Setup(s => s.DeleteResourceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateController().Delete("acme", "does-not-exist", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    /// <summary>
    /// The store scopes deletion by (tenant, resourceId) together - this only proves the controller
    /// forwards the header's tenant into that call, not a tenant a caller could put in the body or URL.
    /// </summary>
    [Fact]
    public async Task Delete_PassesTheCallersTenant_NeverAnotherOne()
    {
        _mockStore.Setup(s => s.DeleteResourceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await CreateController().Delete("tenant-from-header", "r1", CancellationToken.None);

        _mockStore.Verify(s => s.DeleteResourceAsync("tenant-from-header", "r1", It.IsAny<CancellationToken>()), Times.Once);
    }
}
