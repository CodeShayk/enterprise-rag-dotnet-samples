using Contoso.Corpus;

namespace Ch04.Knowledge.Core.Ports;

// Ingestion plane: from source systems to an index.

/// <summary>Reads source documents, with their permissions, from wherever they live.</summary>
public interface IDocumentSource
{
    IAsyncEnumerable<SourceDocument> ReadAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>Splits a source document into chunks.</summary>
public interface IChunker
{
    IEnumerable<Chunk> Split(SourceDocument document);
}

/// <summary>Stores chunks so that a retriever can find them.</summary>
public interface IIndexWriter
{
    Task WriteAsync(IReadOnlyList<Chunk> chunks, CancellationToken cancellationToken = default);
}

// Query plane: from a question to a cited answer.

/// <summary>
/// Finds the chunks that best match a question. Contract: every chunk returned is one the asker
/// may read, and the permission check happens before ranking, so the top k are the asker's top k.
/// </summary>
public interface IRetriever
{
    Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
        string question, Person asker, int top, CancellationToken cancellationToken = default);
}

/// <summary>Reorders retrieved candidates for precision and keeps the best few.</summary>
public interface IReranker
{
    Task<IReadOnlyList<RetrievedChunk>> RerankAsync(
        string question, IReadOnlyList<RetrievedChunk> candidates, int top, CancellationToken cancellationToken = default);
}

/// <summary>Writes an answer from the evidence, as a stream of text.</summary>
public interface IAnswerGenerator
{
    IAsyncEnumerable<string> GenerateAsync(
        string question, IReadOnlyList<RetrievedChunk> evidence, CancellationToken cancellationToken = default);
}
