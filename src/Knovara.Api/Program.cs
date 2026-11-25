using Knovara.Core.Contracts;
using Knovara.ContextMemory;
using Knovara.ContextOptimization;
using Knovara.Evaluation;
using Knovara.Indexing;
using Knovara.Local;
using Knovara.Ingestion;
using Knovara.KnowledgeRepresentation;
using Knovara.Models;
using Knovara.Retrieval;
using Npgsql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Knovara.Api.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Runtime:Mode picks the storage and processing stack. Server (default): Postgres, RabbitMQ, the Docling worker and the model
// service container. Local: one process with SQLite, an in-process queue and extractors, and in-process ONNX models, with no
// Docker and no network. Retrieval, context optimization and every controller are the same in both.
var runtimeMode = builder.Configuration["Runtime:Mode"] ?? "Server";
var localMode = runtimeMode.Equals("Local", StringComparison.OrdinalIgnoreCase);
if (!localMode && !runtimeMode.Equals("Server", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException($"Runtime:Mode must be 'Local' or 'Server', not '{runtimeMode}'.");
}
var tenantAuth = builder.Configuration.GetSection("Authentication").Get<TenantAuthOptions>() ?? new TenantAuthOptions();

// Add services to the container.

builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Knovara API";
        return Task.CompletedTask;
    });

    // The schema generator reflects the CLR enum (an int) and does not consult AddJsonOptions'
    // JsonStringEnumConverter above, so every enum here showed as "integer" with no listed values -
    // actively wrong, since every enum (SourceType, RetrievalMode, ...) is sent and returned as its
    // member name, e.g. "Document" or "Adaptive". Rewriting it here is what keeps Swagger's generated
    // request bodies and "Try it out" usable instead of misleading.
    options.AddSchemaTransformer((schema, context, _) =>
    {
        if (context.JsonTypeInfo.Type.IsEnum)
        {
            schema.Type = Microsoft.OpenApi.JsonSchemaType.String;
            schema.Enum = Enum.GetNames(context.JsonTypeInfo.Type)
                .Select(name => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(name))
                .ToList();

            // A property with e.g. "RetrievalMode Mode = RetrievalMode.Adaptive" otherwise shows the
            // default as the ordinal 4 right next to the string values just rewritten above.
            if (schema.Default is not null)
            {
                var ordinal = schema.Default.GetValue<int>();
                schema.Default = System.Text.Json.Nodes.JsonValue.Create(Enum.GetName(context.JsonTypeInfo.Type, ordinal));
            }
        }
        return Task.CompletedTask;
    });
});

// Knovara pipeline services (see system_architecture.md "Core Components").
// Context memory and evaluation are still in-memory (Phase 3 moves memory to Redis).
if (localMode)
{
    builder.Services.AddKnovaraLocal(builder.Configuration);

    // Set when the CLI or the MCP server launched this runtime hidden (no console): there is otherwise
    // nowhere to look when ingestion or a query misbehaves.
    if (builder.Configuration["Local:LogFile"] is { Length: > 0 } logFile)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new Knovara.Local.Diagnostics.FileLoggerProvider(logFile));
    }
}
else
{
    var postgres = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required. See README.md \"Run locally\".");
    var modelService = builder.Configuration.GetSection("ModelService").Get<ModelServiceOptions>() ?? new ModelServiceOptions();

    builder.Services.AddSingleton(NpgsqlDataSource.Create(postgres));
    builder.Services.AddSingleton(builder.Configuration.GetSection("RabbitMq").Get<RabbitMqOptions>() ?? new RabbitMqOptions());
    builder.Services.AddSingleton<IIngestionQueue, RabbitMqIngestionQueue>();
    builder.Services.AddSingleton<IKnowledgeStore, PostgresKnowledgeStore>();
    builder.Services.AddSingleton<IIndexManagementService, PostgresIndexManagementService>();
    builder.Services.AddHttpClient<IModelService, HttpModelService>(client =>
    {
        client.BaseAddress = new Uri(modelService.BaseUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(modelService.TimeoutSeconds);
    });
}
builder.Services.AddSingleton<IKnowledgeRepresentationService, HierarchyBuilder>();
builder.Services.AddSingleton(builder.Configuration.GetSection("Retrieval").Get<RetrievalOptions>() ?? new RetrievalOptions());
builder.Services.AddSingleton<IIntelligentRetrievalService, ProgressiveRetrievalService>();
builder.Services.AddSingleton<IContextOptimizationService, TokenBudgetContextOptimizationService>();
builder.Services.AddSingleton<IContextMemoryService, InMemoryContextMemoryService>();
builder.Services.AddSingleton<IEvaluationService, InMemoryEvaluationService>();

// Authentication is off by default: the tenant then comes from a header, which is only safe locally
// or behind a trusted gateway. Turning it on makes TenantAuthMiddleware reject untokened requests.
builder.Services.AddSingleton(tenantAuth);
if (tenantAuth.Enabled)
{
    if (string.IsNullOrWhiteSpace(tenantAuth.SigningKey)
        || Encoding.UTF8.GetByteCount(tenantAuth.SigningKey) < TenantAuthOptions.MinimumSigningKeyBytes)
    {
        // Fail at startup rather than on the first request that tries to validate a token.
        throw new InvalidOperationException(
            $"Authentication:SigningKey must be at least {TenantAuthOptions.MinimumSigningKeyBytes} bytes when "
            + "Authentication:Enabled is true. Supply it from configuration or a secret store, never from source.");
    }

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            // Off, or inbound claims are renamed to legacy URIs ("tid" becomes
            // .../identity/claims/tenantid) and Authentication:TenantClaim would not match the token.
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = tenantAuth.Issuer is not null,
                ValidIssuer = tenantAuth.Issuer,
                ValidateAudience = tenantAuth.Audience is not null,
                ValidAudience = tenantAuth.Audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(tenantAuth.SigningKey))
            };
        });
}

var app = builder.Build();

if (tenantAuth.Enabled)
{
    app.UseAuthentication();
}

// Must run before the controllers that bind TenantHeader, and covers every route including the
// worker-facing ones, so no endpoint can be left unprotected by a forgotten attribute.
app.UseMiddleware<TenantAuthMiddleware>();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    // Renders the JSON MapOpenApi already serves - no second spec generator, so the two can never
    // drift apart. Scoped to Development, same as MapOpenApi: this is a developer tool, not a route
    // this project's own no-public-API-surface-by-default posture wants exposed by default.
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "Knovara API v1");
        options.RoutePrefix = "swagger";
    });
}

app.UseAuthorization();

app.MapControllers();

app.Run();
