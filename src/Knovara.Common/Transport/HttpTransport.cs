using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Knovara.Common.Transport
{
    public class HttpTransport : ITransport
    {
        private readonly HttpClient _client;
        private readonly string _baseUrl;
        private readonly string? _jwtToken;
        private readonly ILogger<HttpTransport> _log;

        public HttpTransport(HttpClient client, IConfiguration cfg, ILogger<HttpTransport> log)
        {
            _client = client;
            _log = log;
            _baseUrl = cfg["BaseUrl"]?.TrimEnd('/') ?? "http://localhost:5299";
            _jwtToken = cfg["JwtToken"];
        }

        public async Task<TResponse> SendAsync<TRequest, TResponse>(TRequest request)
            where TRequest : class
            where TResponse : class
        {
            var endpoint = $"{_baseUrl}/{typeof(TRequest).Name.Replace("Request", "").ToLowerInvariant()}";
            var httpReq = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(request)
            };
            if (!string.IsNullOrEmpty(_jwtToken))
                httpReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _jwtToken);

            var resp = await _client.SendAsync(httpReq);
            resp.EnsureSuccessStatusCode();
            var result = await resp.Content.ReadFromJsonAsync<TResponse>();
            return result ?? throw new System.InvalidOperationException("Empty response");
        }
    }
}
