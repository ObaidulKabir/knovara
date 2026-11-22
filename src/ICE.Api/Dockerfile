FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ICE.slnx .
COPY src/ICE.Api/ICE.Api.csproj src/ICE.Api/
COPY src/ICE.Core/ICE.Core.csproj src/ICE.Core/
COPY src/ICE.Ingestion/ICE.Ingestion.csproj src/ICE.Ingestion/
COPY src/ICE.KnowledgeRepresentation/ICE.KnowledgeRepresentation.csproj src/ICE.KnowledgeRepresentation/
COPY src/ICE.Indexing/ICE.Indexing.csproj src/ICE.Indexing/
COPY src/ICE.Retrieval/ICE.Retrieval.csproj src/ICE.Retrieval/
COPY src/ICE.ContextOptimization/ICE.ContextOptimization.csproj src/ICE.ContextOptimization/
COPY src/ICE.ContextMemory/ICE.ContextMemory.csproj src/ICE.ContextMemory/
COPY src/ICE.Evaluation/ICE.Evaluation.csproj src/ICE.Evaluation/
RUN dotnet restore src/ICE.Api/ICE.Api.csproj

COPY src/ src/
RUN dotnet publish src/ICE.Api/ICE.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "ICE.Api.dll"]
