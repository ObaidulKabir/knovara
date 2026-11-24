using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Knovara.Common.Transport
{
    /// <summary>
    /// MCP-flavoured transport that communicates over HTTP using the MCP JSON envelope format.
    /// Uses a plain HttpClient so there is no dependency on a proprietary client package.
    /// </summary>
    public class McpTransport : ITransport
    {
        private readonly HttpClient _client;
        private readonly string _baseUrl;
        private readonly string? _jwtToken;
        private readonly ILogger<McpTransport> _log;

        public McpTransport(HttpClient client, IConfiguration cfg, ILogger<McpTransport> log)
        {
            _client  = client;
            _log     = log;
            var port = cfg.GetValue<int>("McpPort", 50052);
            _baseUrl = cfg["McpBaseUrl"]?.TrimEnd('/') ?? $"http://localhost:{port}/mcp";
            _jwtToken = cfg["JwtToken"];
        }

        public async Task<TResponse> SendAsync<TRequest, TResponse>(TRequest request)
            where TRequest  : class
            where TResponse : class
        {
            var toolName = typeof(TRequest).Name.Replace("Request", "", StringComparison.OrdinalIgnoreCase)
                                                 .ToLowerInvariant();

            var envelope = new { tool = toolName, arguments = request };

            var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/call")
            {
                Content = JsonContent.Create(envelope)
            };

            if (!string.IsNullOrEmpty(_jwtToken))
                httpReq.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _jwtToken);

            _log.LogDebug("MCP call: {Tool}", toolName);

            var resp = await _client.SendAsync(httpReq);
            resp.EnsureSuccessStatusCode();

            var result = await resp.Content.ReadFromJsonAsync<TResponse>();
            return result ?? throw new InvalidOperationException("Empty MCP response");
        }
    }
}
