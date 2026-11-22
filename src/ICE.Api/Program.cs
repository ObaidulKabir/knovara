using ICE.Core.Contracts;
using ICE.ContextMemory;
using ICE.ContextOptimization;
using ICE.Evaluation;
using ICE.Indexing;
using ICE.Ingestion;
using ICE.KnowledgeRepresentation;
using ICE.Retrieval;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// ICRO pipeline services (see system_architecture.md "Core Components").
// Registered as singletons because the current implementations hold in-memory
// state; swap for scoped/transient once backed by Postgres/Redis.
builder.Services.AddSingleton<IResourceIngestionService, InMemoryResourceIngestionService>();
builder.Services.AddSingleton<IKnowledgeRepresentationService, BasicKnowledgeRepresentationService>();
builder.Services.AddSingleton<IIndexManagementService, InMemoryIndexManagementService>();
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

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
