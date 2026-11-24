#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;
using Knovara.Common.Transport;

namespace Knovara.Common.Tests.Transport
{
    // ─────────────────────────────────────────────────────────────────────────────
    // Shared helpers
    // ─────────────────────────────────────────────────────────────────────────────

    public record EchoRequest(string Message);
    public record EchoResponse(string Reply);

    internal static class MockHttp
    {
        /// <summary>Creates a mocked HttpMessageHandler that returns <paramref name="payload"/> as JSON.</summary>
        public static Mock<HttpMessageHandler> RespondWith<T>(T payload, HttpStatusCode status = HttpStatusCode.OK)
        {
            var mock = new Mock<HttpMessageHandler>();
            mock.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(status)
                {
                    Content = JsonContent.Create(payload)
                });
            return mock;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // HttpTransport Tests
    // ─────────────────────────────────────────────────────────────────────────────

    public class HttpTransportTests
    {
        private static IConfiguration BuildConfig(string baseUrl = "http://api.test", string? jwt = null)
        {
            var dict = new System.Collections.Generic.Dictionary<string, string?>
            {
                ["BaseUrl"] = baseUrl
            };
            if (jwt is not null) dict["JwtToken"] = jwt;
            return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        }

        [Fact]
        public async Task SendAsync_ReturnsDeserializedResponse()
        {
            // Arrange
            var expected = new EchoResponse("hello");
            var handler  = MockHttp.RespondWith(expected);
            var client   = new HttpClient(handler.Object);
            var cfg      = BuildConfig();
            var sut      = new HttpTransport(client, cfg, NullLogger<HttpTransport>.Instance);

            // Act
            var result = await sut.SendAsync<EchoRequest, EchoResponse>(new EchoRequest("hi"));

            // Assert
            Assert.Equal("hello", result.Reply);
        }

        [Fact]
        public async Task SendAsync_AttachesBearerToken_WhenJwtConfigured()
        {
            // Arrange
            var handler  = MockHttp.RespondWith(new EchoResponse("ok"));
            var client   = new HttpClient(handler.Object);
            var cfg      = BuildConfig(jwt: "my-token");
            var sut      = new HttpTransport(client, cfg, NullLogger<HttpTransport>.Instance);

            // Act
            await sut.SendAsync<EchoRequest, EchoResponse>(new EchoRequest("x"));

            // Assert – the outgoing request had an Authorization header
            handler.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.Headers.Authorization != null &&
                    r.Headers.Authorization.Scheme == "Bearer" &&
                    r.Headers.Authorization.Parameter == "my-token"),
                ItExpr.IsAny<CancellationToken>());
        }

        [Fact]
        public async Task SendAsync_ThrowsOnNonSuccessStatus()
        {
            // Arrange
            var handler = MockHttp.RespondWith("", HttpStatusCode.InternalServerError);
            var client  = new HttpClient(handler.Object);
            var sut     = new HttpTransport(client, BuildConfig(), NullLogger<HttpTransport>.Instance);

            // Act & Assert
            await Assert.ThrowsAsync<HttpRequestException>(
                () => sut.SendAsync<EchoRequest, EchoResponse>(new EchoRequest("x")));
        }

        [Fact]
        public async Task SendAsync_UsesDefaultBaseUrl_WhenNotConfigured()
        {
            // Arrange – omit BaseUrl from config so the default kicks in
            var cfg = new ConfigurationBuilder().Build();
            var capturedUri = (Uri?)null;
            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUri = req.RequestUri)
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new EchoResponse("r"))
                });

            var sut = new HttpTransport(new HttpClient(handlerMock.Object), cfg, NullLogger<HttpTransport>.Instance);

            // Act
            await sut.SendAsync<EchoRequest, EchoResponse>(new EchoRequest("?"));

            // Assert
            Assert.NotNull(capturedUri);
            Assert.Contains("localhost:5299", capturedUri!.Host + ":" + capturedUri.Port);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // McpTransport Tests
    // ─────────────────────────────────────────────────────────────────────────────

    public class McpTransportTests
    {
        private static IConfiguration BuildConfig(int port = 50052, string? jwt = null)
        {
            var dict = new System.Collections.Generic.Dictionary<string, string?>
            {
                ["McpPort"] = port.ToString()
            };
            if (jwt is not null) dict["JwtToken"] = jwt;
            return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        }

        [Fact]
        public async Task SendAsync_CallsMcpEndpoint()
        {
            // Arrange
            var expected = new EchoResponse("mcp-reply");
            var handler  = MockHttp.RespondWith(expected);
            var client   = new HttpClient(handler.Object);
            var sut      = new McpTransport(client, BuildConfig(), NullLogger<McpTransport>.Instance);

            // Act
            var result = await sut.SendAsync<EchoRequest, EchoResponse>(new EchoRequest("ping"));

            // Assert
            Assert.Equal("mcp-reply", result.Reply);
        }

        [Fact]
        public async Task SendAsync_WrapsRequestInMcpEnvelope()
        {
            // Arrange – capture the outgoing body
            var body = string.Empty;
            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Callback<HttpRequestMessage, CancellationToken>(async (req, _) =>
                {
                    body = await req.Content!.ReadAsStringAsync();
                })
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new EchoResponse("ok"))
                });

            var sut = new McpTransport(new HttpClient(handlerMock.Object), BuildConfig(), NullLogger<McpTransport>.Instance);

            // Act
            await sut.SendAsync<EchoRequest, EchoResponse>(new EchoRequest("hello"));

            // Assert – body should contain "tool" key (MCP envelope)
            var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.TryGetProperty("tool", out _), "MCP envelope must contain 'tool' field");
            Assert.True(doc.RootElement.TryGetProperty("arguments", out _), "MCP envelope must contain 'arguments' field");
        }

        [Fact]
        public async Task SendAsync_AttachesBearerToken_WhenJwtConfigured()
        {
            // Arrange
            var handler = MockHttp.RespondWith(new EchoResponse("ok"));
            var client  = new HttpClient(handler.Object);
            var sut     = new McpTransport(client, BuildConfig(jwt: "tok"), NullLogger<McpTransport>.Instance);

            // Act
            await sut.SendAsync<EchoRequest, EchoResponse>(new EchoRequest("x"));

            // Assert
            handler.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.Headers.Authorization != null &&
                    r.Headers.Authorization.Parameter == "tok"),
                ItExpr.IsAny<CancellationToken>());
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // ITransport contract (interface smoke tests)
    // ─────────────────────────────────────────────────────────────────────────────

    public class ITransportContractTests
    {
        [Fact]
        public async Task MockTransport_FulfillsITransportContract()
        {
            // Arrange – create a simple mock implementation
            var mockTransport = new Mock<ITransport>();
            mockTransport
                .Setup(t => t.SendAsync<EchoRequest, EchoResponse>(It.IsAny<EchoRequest>()))
                .ReturnsAsync(new EchoResponse("contract-ok"));

            ITransport transport = mockTransport.Object;

            // Act
            var result = await transport.SendAsync<EchoRequest, EchoResponse>(new EchoRequest("test"));

            // Assert
            Assert.Equal("contract-ok", result.Reply);
            mockTransport.Verify(t => t.SendAsync<EchoRequest, EchoResponse>(It.IsAny<EchoRequest>()), Times.Once);
        }
    }
}
