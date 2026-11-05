using Microsoft.Extensions.AI;

namespace Contoso.Knowledge.Embeddings;

/// <summary>
/// An <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> over an <see cref="OnnxEmbeddingModel"/> for one side of
/// retrieval: queries or passages, because the model card decides the prefix each side gets. Both sides can share
/// one model; this generator doesn't own it.
/// </summary>
public sealed class OnnxEmbeddingGenerator(OnnxEmbeddingModel model, bool queries = false, int batchSize = 32) : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly EmbeddingGeneratorMetadata _metadata = new("onnx", defaultModelId: model.Spec.Name, defaultModelDimensions: model.Spec.Dimensions);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var prefix = queries ? model.Spec.QueryPrefix : model.Spec.PassagePrefix;
        var embeddings = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var batch in values.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var vector in model.Embed([.. batch.Select(text => prefix + text)]))
            {
                embeddings.Add(new Embedding<float>(vector) { ModelId = model.Spec.Name });
            }
        }

        return Task.FromResult(embeddings);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
    }
}
