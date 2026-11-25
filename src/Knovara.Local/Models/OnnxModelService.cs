using Knovara.Core.Contracts;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Knovara.Local.Models;

public sealed class OnnxModelOptions
{
    /// <summary>Folder holding <c>bge-small/</c> and <c>minilm-rerank/</c>, each with model.onnx and tokenizer.json.</summary>
    public string ModelDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".knovara", "models");

    // The names the server path stores with each embedding, so a corpus embedded there stays valid here (and the reverse).
    public string EmbeddingModel { get; set; } = "BAAI/bge-small-en-v1.5";
    public string RerankModel { get; set; } = "Xenova/ms-marco-MiniLM-L-6-v2";

    /// <summary>The instruction BGE expects before a search question; passages get none.</summary>
    public string QueryPrefix { get; set; } = "Represent this sentence for searching relevant passages: ";

    /// <summary>0 lets ONNX Runtime choose.</summary>
    public int Threads { get; set; }
}

/// <summary>
/// The local runtime's model service: the same two open models as services/model-service (bge-small embeddings, MiniLM
/// cross-encoder), run in-process through ONNX Runtime, so there is no model container and no network. Sessions load on
/// first use, so a runtime that is only listing or deleting documents holds none of the model memory.
/// </summary>
public sealed class OnnxModelService(OnnxModelOptions options) : IModelService, IDisposable
{
    private const int MaxTokens = 512;
    private const int MaxTextChars = 8000;
    private const int BatchSize = 8;   // a batch pads to its longest text, so small batches bound peak memory

    private readonly Lazy<Encoder> _embedder = new(() => new Encoder(options, "bge-small"), LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly Lazy<Encoder> _reranker = new(() => new Encoder(options, "minilm-rerank"), LazyThreadSafetyMode.ExecutionAndPublication);
    private ModelInfo? _info;

    public Task<ModelInfo> GetInfoAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        if (_info is not null) return _info;
        var dimensions = Embed(["probe"], query: false)[0].Length;
        return _info = new ModelInfo(options.EmbeddingModel, dimensions, options.RerankModel);
    }, ct);

    public Task<IReadOnlyList<float[]>> EmbedPassagesAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<float[]>>(() => Embed(texts, query: false, ct), ct);

    public Task<float[]> EmbedQueryAsync(string question, CancellationToken ct = default) =>
        Task.Run(() => Embed([question], query: true, ct)[0], ct);

    public Task<IReadOnlyList<double>> RerankAsync(string question, IReadOnlyList<string> documents, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<double>>(() => Rerank(question, documents, ct), ct);

    // ---------------------------------------------------------------- embeddings

    private float[][] Embed(IReadOnlyList<string> texts, bool query, CancellationToken ct = default)
    {
        var encoder = _embedder.Value;
        var prepared = texts.Select(t => Clip(t)).Select(t => query ? options.QueryPrefix + t : t).ToArray();
        var encoded = prepared.Select(t => encoder.Tokenizer.EncodeSingle(t, MaxTokens)).ToArray();

        var vectors = new float[texts.Count][];
        foreach (var batch in ByLength(encoded.Select(e => e.Length).ToArray()).Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var rows = batch.Select(i => (encoded[i], new int[encoded[i].Length])).ToArray();
            using var output = encoder.Run(rows);
            var tensor = output.First().AsTensor<float>();

            for (var b = 0; b < batch.Length; b++)
            {
                // Pooling is CLS: the first token's hidden state, L2-normalised (what fastembed does for bge).
                var dim = tensor.Dimensions[^1];
                var vector = new float[dim];
                vectors[batch[b]] = vector;
                for (var d = 0; d < dim; d++) vector[d] = tensor.Dimensions.Length == 3 ? tensor[b, 0, d] : tensor[b, d];
                Normalize(vector);
            }
        }
        return vectors;
    }

    // ---------------------------------------------------------------- reranking

    private double[] Rerank(string question, IReadOnlyList<string> documents, CancellationToken ct)
    {
        if (documents.Count == 0) return [];
        var encoder = _reranker.Value;
        var q = Clip(question);
        var pairs = documents.Select(d => encoder.Tokenizer.EncodePair(q, Clip(d), MaxTokens)).ToArray();

        var scores = new double[documents.Count];
        foreach (var batch in ByLength(pairs.Select(p => p.Ids.Length).ToArray()).Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            using var output = encoder.Run(batch.Select(i => (pairs[i].Ids, pairs[i].Types)).ToArray());
            var logits = output.First().AsTensor<float>();
            for (var b = 0; b < batch.Length; b++) scores[batch[b]] = logits.Dimensions.Length == 1 ? logits[b] : logits[b, 0];
        }
        return scores;
    }

    // ---------------------------------------------------------------- helpers

    private static string Clip(string text) => text.Length > MaxTextChars ? text[..MaxTextChars] : text;

    /// <summary>Indexes ordered by length, so each small batch pads little.</summary>
    private static int[] ByLength(int[] lengths) => Enumerable.Range(0, lengths.Length).OrderBy(i => lengths[i]).ToArray();

    private static void Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += (double)x * x;
        var norm = (float)Math.Sqrt(sum);
        if (norm > 1e-12f) for (var i = 0; i < v.Length; i++) v[i] /= norm;
    }

    public void Dispose()
    {
        if (_embedder.IsValueCreated) _embedder.Value.Dispose();
        if (_reranker.IsValueCreated) _reranker.Value.Dispose();
    }

    /// <summary>One model: its tokenizer and ONNX session, plus padding of a batch into the tensors the model expects.</summary>
    private sealed class Encoder : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly bool _wantsTypes;

        public WordPieceTokenizer Tokenizer { get; }

        public Encoder(OnnxModelOptions options, string folder)
        {
            var dir = Path.Combine(options.ModelDirectory, folder);
            var modelPath = Path.Combine(dir, "model.onnx");
            if (!File.Exists(modelPath))
                throw new FileNotFoundException($"Model file not found: {modelPath}. Set Local:ModelDirectory to the folder holding the models.");

            Tokenizer = WordPieceTokenizer.FromTokenizerJson(Path.Combine(dir, "tokenizer.json"));
            using var so = new SessionOptions();
            if (options.Threads > 0) so.IntraOpNumThreads = options.Threads;
            _session = new InferenceSession(modelPath, so);
            _wantsTypes = _session.InputMetadata.ContainsKey("token_type_ids");
        }

        public IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run((int[] Ids, int[] Types)[] rows)
        {
            var width = rows.Max(r => r.Ids.Length);
            var ids = new DenseTensor<long>([rows.Length, width]);
            var mask = new DenseTensor<long>([rows.Length, width]);
            var types = new DenseTensor<long>([rows.Length, width]);
            for (var r = 0; r < rows.Length; r++)
            {
                for (var c = 0; c < width; c++)
                {
                    if (c < rows[r].Ids.Length) { ids[r, c] = rows[r].Ids[c]; mask[r, c] = 1; types[r, c] = rows[r].Types[c]; }
                    else ids[r, c] = Tokenizer.PadId;
                }
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", ids),
                NamedOnnxValue.CreateFromTensor("attention_mask", mask)
            };
            if (_wantsTypes) inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", types));
            return _session.Run(inputs);
        }

        public void Dispose() => _session.Dispose();
    }
}
