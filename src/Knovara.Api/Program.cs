using Knovara.Core.Contracts;
using Knovara.ContextMemory;
using Knovara.ContextOptimization;
using Knovara.Evaluation;
using Knovara.Indexing;
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

var postgres = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required. See README.md \"Run locally\".");
var modelService = builder.Configuration.GetSection("ModelService").Get<ModelServiceOptions>() ?? new ModelServiceOptions();
var tenantAuth = builder.Configuration.GetSection("Authentication").Get<TenantAuthOptions>() ?? new TenantAuthOptions();

// Add services to the container.

builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "Knovara API";
    return Task.CompletedTask;
}));

// Knovara pipeline services (see system_architecture.md "Core Components").
// Context memory and evaluation are still in-memory (Phase 3 moves memory to Redis).
builder.Services.AddSingleton(NpgsqlDataSource.Create(postgres));
builder.Services.AddSingleton(builder.Configuration.GetSection("RabbitMq").Get<RabbitMqOptions>() ?? new RabbitMqOptions());
builder.Services.AddSingleton<IIngestionQueue, RabbitMqIngestionQueue>();
builder.Services.AddSingleton<IKnowledgeStore, PostgresKnowledgeStore>();
builder.Services.AddSingleton<IKnowledgeRepresentationService, HierarchyBuilder>();
builder.Services.AddSingleton<IIndexManagementService, PostgresIndexManagementService>();
builder.Services.AddHttpClient<IModelService, HttpModelService>(client =>
{
    client.BaseAddress = new Uri(modelService.BaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(modelService.TimeoutSeconds);
});
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
}

app.UseAuthorization();

app.MapControllers();

app.Run();
