using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Knovara.Core.Models;
using Knovara.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Knovara.Api.Tests;

public class McpServerTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (KnovaraTools Tools, FakeHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") };
        return (new KnovaraTools(new KnovaraApiClient(http)), handler);
    }

    private const string TwoPassages = """
        {"queryId":"q1","estimatedTokenCount":40,"evidence":[
          {"nodeId":"n1","resourceId":"res-1","title":"Retry policy","content":"Retries back off to 30s.","sourceVersion":"v3","retrievedVia":"Semantic","relevanceScore":0.91},
          {"nodeId":"n2","resourceId":"res-2","title":"Limits","content":"Max 5 attempts.","sourceVersion":"v1","retrievedVia":"Lexical","relevanceScore":0.5}]}
        """;

    [Fact]
    public async Task Ask_PostsQuestionAndBudget_AndReturnsCitedPassages()
    {
        var (tools, handler) = Create(_ => Json(HttpStatusCode.OK, TwoPassages));

        var text = await tools.Ask("retry interval?", tokenBudget: 500);

        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal("http://api.test/api/retrieval/ask", request.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("retry interval?", doc.RootElement.GetProperty("question").GetString());
        Assert.Equal(500, doc.RootElement.GetProperty("tokenBudget").GetInt32());

        Assert.Contains("2 passage(s)", text);
        Assert.Contains("Retry policy - source res-1 (version v3", text);
        Assert.Contains("Retries back off to 30s.", text);
    }

    [Fact]
    public async Task Ask_WithNoEvidence_SaysSoInsteadOfReturningEmptyText()
    {
        var (tools, _) = Create(_ => Json(HttpStatusCode.OK, """{"queryId":"q","evidence":[],"estimatedTokenCount":0}"""));
        Assert.StartsWith("No relevant evidence found", await tools.Ask("anything"));
    }

    [Fact]
    public async Task Search_NormalisesModeCase_AndReportsSufficiencyAndHint()
    {
        var (tools, handler) = Create(_ => Json(HttpStatusCode.OK,
            """{"queryId":"q","evidence":[],"evidenceSufficient":false,"followUpSearchHint":"try the error code","reranked":true}"""));

        var text = await tools.Search("timeouts", mode: "hybridrerank", topK: 3);

        using var doc = JsonDocument.Parse(handler.Calls[0].Body!);
        Assert.Equal("HybridRerank", doc.RootElement.GetProperty("mode").GetString());
        Assert.Contains("Evidence sufficient: no", text);
        Assert.Contains("try the error code", text);
    }

    [Theory]
    [InlineData("Fast")]
    [InlineData("Deep")]
    public async Task Search_RejectsUnknownModeWithoutCallingTheApi(string mode)
    {
        var (tools, handler) = Create(_ => Json(HttpStatusCode.OK, "{}"));

        var ex = await Assert.ThrowsAsync<McpException>(() => tools.Search("q", mode));

        Assert.Contains("HybridRerank", ex.Message); // tells the model what is valid
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Ingest_RejectsUnknownTypeAndQueuesKnownOne()
    {
        var (tools, handler) = Create(_ => Json(HttpStatusCode.Accepted,
            """{"resourceId":"r1","type":"Website","uri":"https://x/y","sourceVersion":null,"tenantId":"acme","status":"Pending"}"""));

        await Assert.ThrowsAsync<McpException>(() => tools.Ingest("https://x/y.cs", "Spreadsheet"));
        await Assert.ThrowsAsync<McpException>(() => tools.Ingest("https://x/y.cs", "Code")); // the API rejects Code, so never offer it
        Assert.Empty(handler.Calls);

        var text = await tools.Ingest("https://x/y", "website");
        Assert.Contains("id=r1", text);
        Assert.Contains("status=Pending", text);
    }

    [Fact]
    public async Task Delete_ReportsNotFoundWithoutError_AndEscapesTheId()
    {
        var (tools, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var text = await tools.DeleteDocument("a/b c");

        Assert.Contains("nothing was deleted", text);
        Assert.Equal("http://api.test/api/resources/a%2Fb%20c", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetStatus_ForMissingDocument_IsAnError()
    {
        var (tools, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        await Assert.ThrowsAsync<McpException>(() => tools.GetDocumentStatus("nope"));
    }

    [Fact]
    public async Task ListDocuments_ListsIdsAndStatuses()
    {
        var (tools, _) = Create(_ => Json(HttpStatusCode.OK,
            """[{"resourceId":"r1","type":"Document","uri":"a.pdf","sourceVersion":"v1","tenantId":"acme","status":"Ready"}]"""));

        var text = await tools.ListDocuments();

        Assert.Contains("a.pdf", text);
        Assert.Contains("status=Ready", text);
        Assert.Contains("id=r1", text);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "KNOVARA_JWT")]
    [InlineData(HttpStatusCode.Forbidden, "tenant claim")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task ApiFailures_BecomeReadableToolErrors(HttpStatusCode status, string expected)
    {
        var (tools, _) = Create(_ => new HttpResponseMessage(status) { Content = new StringContent("boom") });
        var ex = await Assert.ThrowsAsync<McpException>(() => tools.ListDocuments());
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task UnreachableApi_BecomesAToolErrorNotACrash()
    {
        var (tools, _) = Create(_ => throw new HttpRequestException("connection refused"));
        var ex = await Assert.ThrowsAsync<McpException>(() => tools.ListDocuments());
        Assert.Contains("Could not reach the Knovara API", ex.Message);
    }

    [Fact]
    public void HttpClient_IsPinnedToTheConfiguredTenantAndToken()
    {
        var http = KnovaraApiClient.CreateHttpClient("http://h:1/", "acme", "tok");

        Assert.Equal("acme", http.DefaultRequestHeaders.GetValues("X-Tenant-Id").Single());
        Assert.Equal("Bearer", http.DefaultRequestHeaders.Authorization!.Scheme);
        Assert.Equal("tok", http.DefaultRequestHeaders.Authorization.Parameter);
    }

    [Fact]
    public void NoToolAcceptsATenantParameter()
    {
        // The tenant is server configuration, never model input.
        var parameters = typeof(KnovaraTools).GetMethods()
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .SelectMany(m => m.GetParameters());

        Assert.DoesNotContain(parameters, p => p.Name!.Contains("tenant", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnlyDeleteIsMarkedDestructive_AndReadToolsAreReadOnly()
    {
        var tools = typeof(KnovaraTools).GetMethods()
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a is not null).Select(a => a!).ToList();

        Assert.Equal(11, tools.Count);
        Assert.Equal(tools.Count, tools.Select(t => t.Name).Distinct().Count());
        // MCP's destructiveHint defaults to true when unset but only applies to tools that can write.
        Assert.Equal(["knovara_delete_document", "knovara_delete_documents"],
            tools.Where(t => !t.ReadOnly && t.Destructive).Select(t => t.Name).Order().ToArray());
        Assert.All(tools.Where(t => t.Name is "knovara_ask" or "knovara_search" or "knovara_list_documents" or "knovara_get_document_status"
                                         or "knovara_get_stats" or "knovara_list_passages"),
            t => Assert.True(t.ReadOnly));
    }

    [Fact]
    public void SearchModes_MatchTheApisRetrievalModes()
        => Assert.Equal(Enum.GetNames<RetrievalMode>().Order(), KnovaraTools.SearchModes.Order());

    [Fact]
    public void SourceTypes_AreTheApisIngestableSourceTypes()
        // Code is in the enum but POST /ingest answers 400 for it (pinned by Ingest_Code_IsRejected...), so it is not offered.
        => Assert.Equal(Enum.GetNames<SourceType>().Where(n => n != nameof(SourceType.Code)).Order(), KnovaraTools.SourceTypes.Order());

    [Fact]
    public void Statuses_MatchTheApisResourceStatuses()
        => Assert.Equal(Enum.GetNames<ResourceStatus>().Order(), KnovaraTools.Statuses.Order());

    // ---- document-volume management tools ----------------------------------------------------

    [Fact]
    public async Task ListDocuments_SendsFiltersAndPaging_UsesTotalHeader_AndSaysHowToGetMore()
    {
        var (tools, handler) = Create(_ =>
        {
            var response = Json(HttpStatusCode.OK,
                """[{"resourceId":"r1","type":"Website","uri":"https://a","sourceVersion":null,"tenantId":"acme","status":"Failed","error":"HTTP 404"}]""");
            response.Headers.Add("X-Total-Count", "37");
            return response;
        });

        var text = await tools.ListDocuments(status: "failed", type: "website", search: "a b", limit: 1, offset: 5);

        Assert.Equal("http://api.test/api/resources?limit=1&offset=5&status=Failed&type=Website&q=a%20b", handler.Calls[0].Request.RequestUri!.AbsoluteUri);
        Assert.Contains("6-6 of 37", text);
        Assert.Contains("reason: HTTP 404", text);
        Assert.Contains("offset=6", text);
    }

    [Theory]
    [InlineData("Broken", null, 50, 0)]
    [InlineData(null, "Code", 50, 0)]
    [InlineData(null, null, 0, 0)]
    [InlineData(null, null, 201, 0)]
    [InlineData(null, null, 10, -1)]
    public async Task ListDocuments_RejectsBadArgumentsWithoutCallingTheApi(string? status, string? type, int limit, int offset)
    {
        var (tools, handler) = Create(_ => Json(HttpStatusCode.OK, "[]"));
        await Assert.ThrowsAsync<McpException>(() => tools.ListDocuments(status, type, null, limit, offset));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task GetStats_SummarisesTheVolume()
    {
        var (tools, _) = Create(_ => Json(HttpStatusCode.OK,
            """{"resources":3,"byStatus":{"Ready":2,"Failed":1},"byType":{"Document":3},"passages":120,"embeddedPassages":118,"contentCharacters":50000,"uploadedBytes":2048}"""));

        var text = await tools.GetStats();

        Assert.Contains("3 document(s): Ready 2, Failed 1", text);
        Assert.Contains("120", text);
        Assert.Contains("2,048", text);
    }

    [Fact]
    public async Task ListPassages_ShowsTheStoredText_AndMissingDocumentIsAnError()
    {
        var (tools, handler) = Create(r => r.RequestUri!.AbsolutePath.Contains("ghost")
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Json(HttpStatusCode.OK, """{"items":[{"nodeId":"n","level":"paragraph","title":"Guide > Retry","content":"Five attempts."}],"total":1}"""));

        var text = await tools.ListPassages("r1", limit: 5);

        Assert.Contains("Guide > Retry", text);
        Assert.Contains("Five attempts.", text);
        Assert.Contains("limit=5&offset=0", handler.Calls[0].Request.RequestUri!.Query);
        await Assert.ThrowsAsync<McpException>(() => tools.ListPassages("ghost"));
    }

    [Fact]
    public async Task UploadDocument_SendsTheTextAsIs_AndRefusesNonTextNames()
    {
        var (tools, handler) = Create(_ => Json(HttpStatusCode.Accepted,
            """{"resourceId":"r9","type":"Document","uri":"upload://runbook.md","sourceVersion":null,"tenantId":"acme","status":"Pending"}"""));

        var text = await tools.UploadDocument("runbook.md", "# Runbook\nKeys rotate every 45 days.");

        using var doc = JsonDocument.Parse(handler.Calls[0].Body!);
        Assert.Equal("runbook.md", doc.RootElement.GetProperty("fileName").GetString());
        Assert.Equal("# Runbook\nKeys rotate every 45 days.", doc.RootElement.GetProperty("content").GetString());
        Assert.Contains("id=r9", text);

        await Assert.ThrowsAsync<McpException>(() => tools.UploadDocument("report.pdf", "x"));
        await Assert.ThrowsAsync<McpException>(() => tools.UploadDocument("a.md", "  "));
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task ReingestDocument_PostsToTheResource_AndUnknownIdIsAnError()
    {
        var (tools, handler) = Create(r => r.RequestUri!.AbsolutePath.Contains("ghost")
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Json(HttpStatusCode.Accepted, """{"resourceId":"r1","type":"Document","uri":"a.md","sourceVersion":null,"tenantId":"acme","status":"Pending"}"""));

        var text = await tools.ReingestDocument("r1");

        Assert.Equal(HttpMethod.Post, handler.Calls[0].Request.Method);
        Assert.EndsWith("/api/resources/r1/reingest", handler.Calls[0].Request.RequestUri!.AbsolutePath);
        Assert.Contains("status=Pending", text);
        await Assert.ThrowsAsync<McpException>(() => tools.ReingestDocument("ghost"));
    }

    [Fact]
    public async Task DeleteDocuments_ByIdsAndStatus_SendsBothAndReportsWhatRemained()
    {
        var (tools, handler) = Create(_ => Json(HttpStatusCode.OK, """{"deleted":["a"],"notDeleted":["b"]}"""));

        var text = await tools.DeleteDocuments(["a", "b", "a"], "failed");

        using var doc = JsonDocument.Parse(handler.Calls[0].Body!);
        Assert.Equal(["a", "b"], doc.RootElement.GetProperty("resourceIds").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("Failed", doc.RootElement.GetProperty("status").GetString());
        Assert.Contains("Deleted 1", text);
        Assert.Contains("Not deleted", text);
        Assert.Contains("b", text);
    }

    [Fact]
    public async Task DeleteDocuments_WithNoSelector_IsRefusedLocally_SoItCanNeverMeanEverything()
    {
        var (tools, handler) = Create(_ => Json(HttpStatusCode.OK, """{"deleted":[],"notDeleted":[]}"""));

        await Assert.ThrowsAsync<McpException>(() => tools.DeleteDocuments());
        await Assert.ThrowsAsync<McpException>(() => tools.DeleteDocuments([]));
        await Assert.ThrowsAsync<McpException>(() => tools.DeleteDocuments(["  "]));
        await Assert.ThrowsAsync<McpException>(() => tools.DeleteDocuments(null, "Everything"));
        Assert.Empty(handler.Calls);
    }
}
