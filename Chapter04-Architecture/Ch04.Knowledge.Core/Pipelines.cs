using System.Runtime.CompilerServices;
using Ch04.Knowledge.Core.Ports;
using Contoso.Corpus;

namespace Ch04.Knowledge.Core;

/// <summary>The ingestion plane in one class: read, chunk, write.</summary>
public sealed class IngestionPipeline(IDocumentSource source, IChunker chunker, IIndexWriter index)
{
    public async Task<(int Documents, int Chunks)> RunAsync(CancellationToken cancellationToken = default)
    {
        var documents = 0;
        var chunks = new List<Chunk>();
        await foreach (var document in source.ReadAllAsync(cancellationToken))
        {
            documents++;
            chunks.AddRange(chunker.Split(document));
        }

        await index.WriteAsync(chunks, cancellationToken);
        return (documents, chunks.Count);
    }
}

/// <summary>The query plane in one class: retrieve, re-rank, generate. It knows only ports.</summary>
public sealed class RagPipeline(IRetriever retriever, IReranker reranker, IAnswerGenerator generator, PipelineOptions options)
{
    public const string NoEvidence = "I couldn't find this in any document you can read, so I won't guess.";

    public async Task<RagAnswer> AskAsync(string question, Person asker, CancellationToken cancellationToken = default)
    {
        var candidates = await retriever.RetrieveAsync(question, asker, options.Candidates, cancellationToken);
        var evidence = await reranker.RerankAsync(question, candidates, options.Top, cancellationToken);

        return evidence.Count == 0
            ? new RagAnswer(evidence, Say(NoEvidence))
            : new RagAnswer(evidence, generator.GenerateAsync(question, evidence, cancellationToken));
    }

    private static async IAsyncEnumerable<string> Say(string text, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield return text;
    }
}
