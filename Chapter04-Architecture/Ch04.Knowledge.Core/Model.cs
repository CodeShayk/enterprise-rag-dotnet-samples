using Contoso.Corpus;

namespace Ch04.Knowledge.Core;

/// <summary>A piece of a source document: the unit the assistant indexes and retrieves.</summary>
public sealed record Chunk(string Id, SourceDocument Document, int Index, string Text);

/// <summary>A chunk retrieved for one question, with the score its retriever gave it.</summary>
public sealed record RetrievedChunk(Chunk Chunk, double Score);

/// <summary>The evidence an answer rests on, and the answer itself as it streams.</summary>
public sealed record RagAnswer(IReadOnlyList<RetrievedChunk> Evidence, IAsyncEnumerable<string> Text);

/// <summary>How many candidates to retrieve and how many to keep as evidence.</summary>
public sealed class PipelineOptions
{
    public int Candidates { get; init; } = 20;

    public int Top { get; init; } = 4;
}
