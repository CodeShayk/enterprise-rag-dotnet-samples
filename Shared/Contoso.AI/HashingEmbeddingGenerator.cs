using System.Numerics.Tensors;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Contoso.AI;

/// <summary>
/// A deterministic, offline stand-in for an embedding model. It hashes every word and the word's
/// character trigrams into a fixed number of dimensions (the "hashing trick") and normalizes the
/// result, so texts that share words or word fragments get similar vectors. It has no notion of
/// meaning: "screws" and "bolts" share nothing. Chapter 10 replaces it with a real embedding
/// model on ONNX Runtime; until then it keeps every sample offline and every output reproducible.
/// </summary>
public sealed partial class HashingEmbeddingGenerator(int dimensions = HashingEmbeddingGenerator.DefaultDimensions)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    public const int DefaultDimensions = 384;

    private readonly EmbeddingGeneratorMetadata _metadata =
        new("contoso-hashing", providerUri: null, defaultModelId: $"hashing-{dimensions}", defaultModelDimensions: dimensions);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var embeddings = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            embeddings.Add(new Embedding<float>(Embed(value)) { ModelId = _metadata.DefaultModelId });
        }

        return Task.FromResult(embeddings);
    }

    /// <summary>One text to one unit-length vector.</summary>
    public float[] Embed(string text)
    {
        var vector = new float[dimensions];
        foreach (Match word in Word().Matches(text.ToLowerInvariant()))
        {
            if (ContosoScripts.IsStopWord(word.Value))
            {
                continue;
            }

            Add(vector, word.Value, 1.0f);

            // Character trigrams with boundary markers: "<flanges>" and "<flange>" share five of them.
            var padded = $"<{word.Value}>";
            for (var i = 0; i + 3 <= padded.Length; i++)
            {
                Add(vector, padded.Substring(i, 3), 0.5f);
            }
        }

        var norm = TensorPrimitives.Norm(vector);
        if (norm > 0)
        {
            TensorPrimitives.Divide(vector, norm, vector);
        }

        return vector;
    }

    // FNV-1a: a stable hash, unlike string.GetHashCode(), which changes from process to process.
    // One bit picks the sign, so colliding features tend to cancel out instead of piling up.
    private static void Add(float[] vector, string feature, float weight)
    {
        var hash = 2166136261u;
        foreach (var c in feature)
        {
            hash = (hash ^ c) * 16777619u;
        }

        var sign = (hash & 1) == 0 ? 1f : -1f;
        vector[(hash >> 1) % (uint)vector.Length] += sign * weight;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:[-.'][\p{L}\p{N}]+)*")]
    private static partial Regex Word();
}
