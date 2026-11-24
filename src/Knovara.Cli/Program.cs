using Knovara.Cli;

var parsed = Args.Parse(args);

if (parsed.Command is null or "help" or "--help" or "-h")
{
    PrintHelp();
    return parsed.Command is null ? 1 : 0;
}

var command = parsed.Command.ToLowerInvariant();
var apiUrl = parsed.Option("api") ?? Environment.GetEnvironmentVariable("KNOVARA_API_URL") ?? "http://127.0.0.1:5299";
var tenant = parsed.Option("tenant") ?? Environment.GetEnvironmentVariable("KNOVARA_TENANT");
if (tenant is null)
{
    Console.Error.WriteLine("Missing tenant. Pass --tenant <id> or set the KNOVARA_TENANT environment variable.");
    return 1;
}

using var client = new ApiClient(apiUrl, tenant);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { cts.Cancel(); e.Cancel = true; };

try
{
    return await (command switch
    {
        "ingest" => Ingest(client, parsed, cts.Token),
        "status" => Status(client, parsed, cts.Token),
        "watch" => Watch(client, parsed, cts.Token),
        "ask" => Ask(client, parsed, cts.Token),
        "search" => Search(client, parsed, cts.Token),
        _ => Unknown(parsed.Command)
    });
}
catch (ApiException ex)
{
    Console.Error.WriteLine($"API error: {ex.Message}");
    return 1;
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"Could not reach the API at {apiUrl} ({ex.Message}). Is it running? See README \"Run locally\".");
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 1;
}

static Task<int> Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'. Run 'knovara help' for usage.");
    return Task.FromResult(1);
}

static async Task<int> Ingest(ApiClient client, Args args, CancellationToken ct)
{
    var uri = args.Positional.Count > 0 ? args.Positional[0] : null;
    if (uri is null)
    {
        Console.Error.WriteLine("Usage: knovara ingest <uri-or-path> [--type Document|Website]");
        return 1;
    }

    var typeInput = args.Option("type") ?? "Document";
    var type = Canonicalize(["Document", "Website"], typeInput);
    if (type is null)
    {
        Console.Error.WriteLine($"Unknown --type '{typeInput}'. Valid: Document, Website.");
        return 1;
    }

    var resource = await client.IngestAsync(uri, type, ct);
    Console.WriteLine($"Queued {resource.ResourceId} ({resource.Type}) — status: {resource.Status}");
    Console.WriteLine($"Check progress:  knovara watch {resource.ResourceId} --tenant {resource.TenantId}");
    return 0;
}

static async Task<int> Status(ApiClient client, Args args, CancellationToken ct)
{
    var resourceId = args.Positional.Count > 0 ? args.Positional[0] : null;
    if (resourceId is null)
    {
        Console.Error.WriteLine("Usage: knovara status <resourceId>");
        return 1;
    }

    var resource = await client.GetResourceAsync(resourceId, ct);
    if (resource is null)
    {
        Console.Error.WriteLine($"No resource '{resourceId}' for this tenant.");
        return 1;
    }

    PrintResource(resource);
    return 0;
}

static async Task<int> Watch(ApiClient client, Args args, CancellationToken ct)
{
    var resourceId = args.Positional.Count > 0 ? args.Positional[0] : null;
    if (resourceId is null)
    {
        Console.Error.WriteLine("Usage: knovara watch <resourceId> [--interval seconds]");
        return 1;
    }
    var interval = TimeSpan.FromSeconds(double.TryParse(args.Option("interval"), out var seconds) ? seconds : 3);

    string? lastStatus = null;
    while (true)
    {
        var resource = await client.GetResourceAsync(resourceId, ct);
        if (resource is null)
        {
            Console.Error.WriteLine($"No resource '{resourceId}' for this tenant.");
            return 1;
        }
        if (resource.Status != lastStatus)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {resource.Status}");
            lastStatus = resource.Status;
        }
        if (resource.Status is "Ready" or "Failed")
        {
            PrintResource(resource);
            return resource.Status == "Ready" ? 0 : 1;
        }
        await Task.Delay(interval, ct);
    }
}

static async Task<int> Ask(ApiClient client, Args args, CancellationToken ct)
{
    var question = args.Text();
    if (string.IsNullOrWhiteSpace(question))
    {
        Console.Error.WriteLine("Usage: knovara ask <question> [--session id] [--budget tokens]");
        return 1;
    }
    var session = args.Option("session") ?? Guid.NewGuid().ToString("n")[..8];
    var budget = int.TryParse(args.Option("budget"), out var tokenBudget) ? tokenBudget : 2000;

    var package = await client.AskAsync(session, question, budget, ct);
    if (package.Evidence.Count == 0)
    {
        Console.WriteLine("No evidence found.");
        return 0;
    }

    foreach (var (item, rank) in package.Evidence.Select((e, i) => (e, i + 1)))
    {
        PrintEvidence(rank, item);
    }
    Console.WriteLine($"\n{package.Evidence.Count} passage(s), ~{package.EstimatedTokenCount} tokens. Session: {session}");
    return 0;
}

static async Task<int> Search(ApiClient client, Args args, CancellationToken ct)
{
    var question = args.Text();
    if (string.IsNullOrWhiteSpace(question))
    {
        Console.Error.WriteLine("Usage: knovara search <question> [--mode Lexical|Semantic|Hybrid|HybridRerank|Adaptive] [--top-k n]");
        return 1;
    }
    var modeInput = args.Option("mode") ?? "Adaptive";
    var mode = Canonicalize(["Lexical", "Semantic", "Hybrid", "HybridRerank", "Adaptive"], modeInput);
    if (mode is null)
    {
        Console.Error.WriteLine($"Unknown --mode '{modeInput}'. Valid: Lexical, Semantic, Hybrid, HybridRerank, Adaptive.");
        return 1;
    }
    var topK = int.TryParse(args.Option("top-k"), out var k) ? k : 5;

    var result = await client.SearchAsync(question, mode, topK, ct);
    if (result.Evidence.Count == 0)
    {
        Console.WriteLine("No results.");
        return 0;
    }
    foreach (var (item, rank) in result.Evidence.Select((e, i) => (e, i + 1)))
    {
        PrintEvidence(rank, item);
    }
    Console.WriteLine($"\nmode: {mode}   reranked: {result.Reranked}");
    return 0;
}

static string? Canonicalize(string[] valid, string input) =>
    valid.FirstOrDefault(v => string.Equals(v, input, StringComparison.OrdinalIgnoreCase));

static void PrintResource(ResourceReference resource)
{
    Console.WriteLine($"{resource.ResourceId}  {resource.Uri}");
    Console.WriteLine($"  type: {resource.Type}   status: {resource.Status}   version: {resource.SourceVersion ?? "(none yet)"}");
    if (resource.Status == "Failed")
    {
        Console.WriteLine("  the API does not expose the failure reason yet; check the worker's logs.");
    }
}

static void PrintEvidence(int rank, EvidenceItem item)
{
    Console.WriteLine($"[{rank}] {item.Title}  ({item.RetrievedVia}, score {item.RelevanceScore:F2})");
    Console.WriteLine($"    {Snippet(item.Content)}");
}

static string Snippet(string content)
{
    var oneLine = content.ReplaceLineEndings(" ").Trim();
    return oneLine.Length <= 160 ? oneLine : oneLine[..160] + "…";
}

static void PrintHelp()
{
    Console.WriteLine("""
        knovara — thin CLI for the Knovara API

        Usage:
          knovara ingest <uri-or-path> [--type Document|Website]
          knovara status <resourceId>
          knovara watch  <resourceId> [--interval seconds]
          knovara ask    <question>   [--session id] [--budget tokens]
          knovara search <question>  [--mode Lexical|Semantic|Hybrid|HybridRerank|Adaptive] [--top-k n]

        Global options (any command):
          --api    <url>   API base URL (default http://127.0.0.1:5299, or $KNOVARA_API_URL)
          --tenant <id>    Tenant id (required: pass --tenant, or set $KNOVARA_TENANT)

        Examples:
          knovara ingest samples/payment-service-guide.md --tenant acme
          knovara watch 22fa4671d52a4ed68dcb56a47d6e2efb --tenant acme
          knovara ask "how long are payment logs retained" --tenant acme
          knovara search "maximum retry interval" --mode Semantic --tenant acme
        """);
}
