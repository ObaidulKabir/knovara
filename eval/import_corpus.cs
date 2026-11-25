// Imports a corpus exported by eval/export_postgres_corpus.py into the local runtime's SQLite database, keeping node ids,
// the hierarchy and the stored embeddings, so the SQLite path can be benchmarked against Postgres on identical data.
//
//   dotnet run eval/import_corpus.cs -- <corpus.jsonl> <knovara.db> <tenant>
#:project ../src/Knovara.Local/Knovara.Local.csproj
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property PublishAot=false

using System.Text.Json;
using Knovara.Core.Models;
using Knovara.Local.Storage;

if (args.Length != 3)
{
    Console.Error.WriteLine("usage: dotnet run eval/import_corpus.cs -- <corpus.jsonl> <knovara.db> <tenant>");
    return 2;
}

var db = new SqliteDatabase(args[1]);
await db.InitializeAsync();
var store = new SqliteKnowledgeStore(db);
var tenant = args[2];

int resources = 0, passages = 0;
foreach (var line in File.ReadLines(args[0]).Where(l => !string.IsNullOrWhiteSpace(l)))
{
    using var doc = JsonDocument.Parse(line);
    var root = doc.RootElement;

    var resource = await store.RegisterResourceAsync(tenant, root.GetProperty("uri").GetString()!, Enum.Parse<SourceType>(root.GetProperty("type").GetString()!));

    var nodes = new List<KnowledgeNode>();
    var embeddings = new Dictionary<string, float[]>();
    string model = "";
    foreach (var n in root.GetProperty("nodes").EnumerateArray())
    {
        var id = n.GetProperty("nodeId").GetString()!;
        string? Str(string p) => n.GetProperty(p).ValueKind == JsonValueKind.Null ? null : n.GetProperty(p).GetString();
        var metadata = n.GetProperty("metadata").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString());

        nodes.Add(new KnowledgeNode(id, resource.ResourceId, Str("parentNodeId"), n.GetProperty("level").GetString()!, n.GetProperty("title").GetString()!,
            Str("summary"), Str("content"), metadata));

        if (n.GetProperty("embedding").ValueKind == JsonValueKind.Array)
        {
            embeddings[id] = n.GetProperty("embedding").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            model = Str("embeddingModel") ?? model;
        }
        if (Str("content") is not null) passages++;
    }

    await store.CommitVersionAsync(tenant, resource.ResourceId, root.GetProperty("sourceVersion").GetString()!, nodes, embeddings, model);
    resources++;
}

Console.WriteLine($"imported {resources} resources, {passages} passages into {args[1]} for tenant '{tenant}'");
return 0;
