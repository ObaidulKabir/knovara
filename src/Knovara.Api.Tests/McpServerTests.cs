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
            """{"resourceId":"r1","type":"Code","uri":"https://x/y.cs","sourceVersion":null,"tenantId":"acme","status":"Pending"}"""));

        await Assert.ThrowsAsync<McpException>(() => tools.Ingest("https://x/y.cs", "Spreadsheet"));
        Assert.Empty(handler.Calls);

        var text = await tools.Ingest("https://x/y.cs", "code");
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

        Assert.Equal(6, tools.Count);
        Assert.Equal(tools.Count, tools.Select(t => t.Name).Distinct().Count());
        // MCP's destructiveHint defaults to true when unset but only applies to tools that can write.
        Assert.Equal(["knovara_delete_document"], tools.Where(t => !t.ReadOnly && t.Destructive).Select(t => t.Name).ToArray());
        Assert.All(tools.Where(t => t.Name is "knovara_ask" or "knovara_search" or "knovara_list_documents" or "knovara_get_document_status"),
            t => Assert.True(t.ReadOnly));
    }

    [Fact]
    public void SearchModes_MatchTheApisRetrievalModes()
        => Assert.Equal(Enum.GetNames<RetrievalMode>().Order(), KnovaraTools.SearchModes.Order());

    [Fact]
    public void SourceTypes_MatchTheApisSourceTypes()
        => Assert.Equal(Enum.GetNames<SourceType>().Order(), KnovaraTools.SourceTypes.Order());
}
