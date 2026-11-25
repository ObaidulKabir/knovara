using System.Numerics.Tensors;
using System.Text.Json;
using Knovara.Local.Models;

namespace Knovara.Local.Tests;

/// <summary>
/// Checks the in-process ONNX models against vectors and scores recorded from services/model-service (the Python
/// fastembed service the server path uses). They need the model files (set KNOVARA_MODEL_DIR, default ~/.knovara/models);
/// without them each test returns early, so a clean checkout still builds and passes.
/// </summary>
public sealed class OnnxModelParityTests
{
    private static readonly string ModelDir = Environment.GetEnvironmentVariable("KNOVARA_MODEL_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".knovara", "models");

    private static bool ModelsPresent => File.Exists(Path.Combine(ModelDir, "bge-small", "model.onnx"))
                                         && File.Exists(Path.Combine(ModelDir, "minilm-rerank", "model.onnx"));

    private sealed record Reference(
        string[] Passages, string[] Queries, float[][] PassageVectors, float[][] QueryVectors,
        string RerankQuery, string[] RerankDocuments, double[] RerankScores);

    private static Reference LoadReference() => JsonSerializer.Deserialize<Reference>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "model_reference.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static OnnxModelService Create() => new(new OnnxModelOptions { ModelDirectory = ModelDir });

    [Fact]
    public async Task Passage_embeddings_match_the_model_service()
    {
        if (!ModelsPresent) return;
        var reference = LoadReference();
        using var models = Create();

        var vectors = await models.EmbedPassagesAsync(reference.Passages);

        for (var i = 0; i < vectors.Count; i++)
        {
            var cosine = TensorPrimitives.CosineSimilarity(vectors[i], reference.PassageVectors[i]);
            Assert.True(cosine > 0.9995f, $"passage {i}: cosine {cosine} vs the model service");
        }
    }

    [Fact]
    public async Task Query_embeddings_match_the_model_service()
    {
        if (!ModelsPresent) return;
        var reference = LoadReference();
        using var models = Create();

        for (var i = 0; i < reference.Queries.Length; i++)
        {
            var vector = await models.EmbedQueryAsync(reference.Queries[i]);
            var cosine = TensorPrimitives.CosineSimilarity(vector, reference.QueryVectors[i]);
            Assert.True(cosine > 0.9995f, $"query {i}: cosine {cosine} vs the model service");
        }
    }

    [Fact]
    public async Task Rerank_scores_match_the_model_service()
    {
        if (!ModelsPresent) return;
        var reference = LoadReference();
        using var models = Create();

        var scores = await models.RerankAsync(reference.RerankQuery, reference.RerankDocuments);

        for (var i = 0; i < scores.Count; i++)
            Assert.True(Math.Abs(scores[i] - reference.RerankScores[i]) < 0.05, $"doc {i}: {scores[i]} vs {reference.RerankScores[i]}");
        Assert.Equal(0, Array.IndexOf(reference.RerankScores, reference.RerankScores.Max()));
        Assert.Equal(0, scores.ToList().IndexOf(scores.Max()));
    }

    [Fact]
    public async Task Info_reports_the_server_models_names_and_dimensions()
    {
        if (!ModelsPresent) return;
        using var models = Create();
        var info = await models.GetInfoAsync();
        Assert.Equal("BAAI/bge-small-en-v1.5", info.EmbeddingModel);
        Assert.Equal(384, info.Dimensions);
    }

    [Theory]
    [InlineData("Hello, World!", "hello , world !")]
    [InlineData("Zoë Müller", "zoe muller")]
    [InlineData("a\tb\nc", "a b c")]
    public void Normalizer_lowercases_strips_accents_and_cleans_whitespace(string input, string expected) =>
        Assert.Equal(expected, string.Join(" ", WordPieceTokenizer.PreTokenize(WordPieceTokenizer.Normalize(input))));

    [Fact]
    public void Chinese_characters_become_separate_words()
    {
        var words = WordPieceTokenizer.PreTokenize(WordPieceTokenizer.Normalize("价格100")).ToList();
        Assert.Equal(["价", "格", "100"], words);
    }
}
