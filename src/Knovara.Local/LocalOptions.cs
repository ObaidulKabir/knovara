namespace Knovara.Local;

/// <summary>Settings for the local runtime, bound from the <c>Local</c> configuration section.</summary>
public sealed class LocalOptions
{
    /// <summary>The one SQLite file holding everything. Defaults to the per-user data folder.</summary>
    public string DatabasePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Knovara", "knovara.db");

    /// <summary>Where the embedding and reranking models live (see <see cref="Models.OnnxModelOptions.ModelDirectory"/>).</summary>
    public string? ModelDirectory { get; set; }

    /// <summary>Optional folder that relative ingest URIs resolve against. Unset means only uploads and http(s) URLs can be ingested.</summary>
    public string? SourceRoot { get; set; }

    public long MaxSourceBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>Allow ingesting URLs on private networks. Off by default so a page cannot make the runtime reach internal services.</summary>
    public bool AllowPrivateUrls { get; set; }

    /// <summary>ONNX Runtime threads for the models; 0 lets it choose.</summary>
    public int ModelThreads { get; set; }
}
