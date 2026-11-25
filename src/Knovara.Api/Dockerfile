FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Knovara.slnx .
COPY src/Knovara.Api/Knovara.Api.csproj src/Knovara.Api/
COPY src/Knovara.Core/Knovara.Core.csproj src/Knovara.Core/
COPY src/Knovara.Ingestion/Knovara.Ingestion.csproj src/Knovara.Ingestion/
COPY src/Knovara.KnowledgeRepresentation/Knovara.KnowledgeRepresentation.csproj src/Knovara.KnowledgeRepresentation/
COPY src/Knovara.Indexing/Knovara.Indexing.csproj src/Knovara.Indexing/
COPY src/Knovara.Retrieval/Knovara.Retrieval.csproj src/Knovara.Retrieval/
COPY src/Knovara.ContextOptimization/Knovara.ContextOptimization.csproj src/Knovara.ContextOptimization/
COPY src/Knovara.ContextMemory/Knovara.ContextMemory.csproj src/Knovara.ContextMemory/
COPY src/Knovara.Evaluation/Knovara.Evaluation.csproj src/Knovara.Evaluation/
COPY src/Knovara.Models/Knovara.Models.csproj src/Knovara.Models/
COPY src/Knovara.Local/Knovara.Local.csproj src/Knovara.Local/
RUN dotnet restore src/Knovara.Api/Knovara.Api.csproj

COPY src/ src/
RUN dotnet publish src/Knovara.Api/Knovara.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Knovara.Api.dll"]
