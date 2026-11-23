using ICE.Core.Contracts;
using ICE.ContextMemory;
using ICE.ContextOptimization;
using ICE.Evaluation;
using ICE.Indexing;
using ICE.Ingestion;
using ICE.KnowledgeRepresentation;
using ICE.Retrieval;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var postgres = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required. See README.md \"Run locally\".");

// Add services to the container.

builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// ICRO pipeline services (see system_architecture.md "Core Components").
// Context memory and evaluation are still in-memory (Phase 3 moves memory to Redis).
builder.Services.AddSingleton(NpgsqlDataSource.Create(postgres));
builder.Services.AddSingleton(builder.Configuration.GetSection("RabbitMq").Get<RabbitMqOptions>() ?? new RabbitMqOptions());
builder.Services.AddSingleton<IIngestionQueue, RabbitMqIngestionQueue>();
builder.Services.AddSingleton<IKnowledgeStore, PostgresKnowledgeStore>();
builder.Services.AddSingleton<IKnowledgeRepresentationService, HierarchyBuilder>();
builder.Services.AddSingleton<IIndexManagementService, PostgresIndexManagementService>();
builder.Services.AddSingleton<IIntelligentRetrievalService, ProgressiveRetrievalService>();
builder.Services.AddSingleton<IContextOptimizationService, TokenBudgetContextOptimizationService>();
builder.Services.AddSingleton<IContextMemoryService, InMemoryContextMemoryService>();
builder.Services.AddSingleton<IEvaluationService, InMemoryEvaluationService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthorization();

app.MapControllers();

app.Run();
