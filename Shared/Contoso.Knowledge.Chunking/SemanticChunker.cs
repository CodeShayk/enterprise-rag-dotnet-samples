using System.Numerics.Tensors;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;

namespace Contoso.Knowledge.Chunking;

/// <summary>
/// Semantic chunking: embed every sentence, measure how far each one is from the next and cut where the distance
/// jumps. A cut goes where the distance is above the given percentile of the document's distances or, with
/// <paramref name="gradient"/>, where the distance rises fastest. A group longer than the limit is cut again at its
/// largest internal distance. The cost is one embedding per sentence, paid again whenever the document changes.
/// Sentences go to the embedder <paramref name="batch"/> at a time: Ollama on Windows refuses a burst of hundreds.
/// </summary>
public sealed class SemanticChunker(
    IEmbeddingGenerator<string, Embedding<float>> embedder, Tokenizer tokenizer, double percentile = 0.9, int max = 300,
    bool gradient = false, string prefix = "", int batch = 32) : IChunker
{
    public string Name => gradient ? $"semantic-gradient-p{percentile * 100:F0}" : $"semantic-p{percentile * 100:F0}";

    /// <summary>Sentences embedded so far, for the CPU budget.</summary>
    public int Embedded { get; private set; }

    public async ValueTask<IReadOnlyList<Chunk>> SplitAsync(ChunkSource source, CancellationToken cancellationToken = default)
    {
        var text = source.Text;
        var sentences = Sentences.Split(text);
        if (sentences.Count == 0) return [];

        var vectors = new List<Embedding<float>>(sentences.Count);
        foreach (var group in sentences.Chunk(batch))
            vectors.AddRange(await embedder.GenerateAsync([.. group.Select(s => prefix + text[s.Start..s.End])], cancellationToken: cancellationToken));
        Embedded += sentences.Count;
        var distances = Enumerable.Range(0, sentences.Count - 1)
            .Select(i => 1 - TensorPrimitives.CosineSimilarity(vectors[i].Vector.Span, vectors[i + 1].Vector.Span)).ToArray();

        // Cut after sentence i when the distance to sentence i + 1 (or its rise over the previous one) is unusual for this document.
        var signal = gradient ? distances.Select((d, i) => i == 0 ? 0 : d - distances[i - 1]).ToArray() : distances;
        var threshold = signal.Length == 0 ? double.MaxValue : signal.Order().ElementAt((int)Math.Floor(percentile * (signal.Length - 1)));
        var groups = new List<(int First, int Last)>();
        var first = 0;
        for (var i = 0; i < distances.Length; i++)
        {
            if (signal[i] > threshold)
            {
                groups.Add((first, i));
                first = i + 1;
            }
        }

        groups.Add((first, sentences.Count - 1));

        var chunks = new List<Chunk>();
        foreach (var (a, b) in groups) Emit(a, b);
        return chunks;

        void Emit(int a, int b)
        {
            var (start, end) = (sentences[a].Start, sentences[b].End);
            if (a == b || Tokens.Count(tokenizer, text, start, end) <= max)
            {
                chunks.Add(new Chunk(source.Id, start, end, text[start..end]));
                return;
            }

            // Too long: cut at the largest distance inside the group.
            var cut = Enumerable.Range(a, b - a).MaxBy(i => distances[i]);
            Emit(a, cut);
            Emit(cut + 1, b);
        }
    }
}
