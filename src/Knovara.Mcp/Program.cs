using Knovara.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Knovara MCP server (stdio). Configuration comes from environment variables so that MCP clients can set
// it in their server config without putting a tenant or token on a command line:
//   KNOVARA_TENANT_ID  (required)  the one tenant this server may read and write
//   KNOVARA_API_URL    (optional)  default http://127.0.0.1:5299
//   KNOVARA_JWT        (optional)  bearer token, when the API has Authentication:Enabled
var tenant = Environment.GetEnvironmentVariable("KNOVARA_TENANT_ID");
if (string.IsNullOrWhiteSpace(tenant))
{
    // stdout is the protocol channel, so diagnostics go to stderr only.
    Console.Error.WriteLine("knovara-mcp: KNOVARA_TENANT_ID is required (the tenant this server is allowed to access).");
    return 2;
}

var apiUrl = Environment.GetEnvironmentVariable("KNOVARA_API_URL") is { Length: > 0 } url ? url : "http://127.0.0.1:5299";
var jwt = Environment.GetEnvironmentVariable("KNOVARA_JWT");

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(new KnovaraApiClient(KnovaraApiClient.CreateHttpClient(apiUrl, tenant, jwt)));
builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "knovara", Version = "1.0.0" })
    .WithStdioServerTransport()
    .WithTools<KnovaraTools>();

await builder.Build().RunAsync();
return 0;
