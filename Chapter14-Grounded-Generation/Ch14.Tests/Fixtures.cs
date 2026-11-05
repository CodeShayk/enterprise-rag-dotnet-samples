using Contoso.AI;
using Contoso.Corpus;
using Contoso.Knowledge.Chunking;
using Contoso.Knowledge.Generation;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;

namespace Ch14.Tests;

/// <summary>A small manual page and the hits a retriever would return for it.</summary>
internal static class Fixtures
{
    public const string Manual = """
        ## 6.3 Flange bolt torque

        Table 6-2 gives the tightening torque for the CP-200 flange bolts.
        | Bolt size | Torque |
        |---|---|
        | M12 | 85 N·m |
        | M16 | 210 N·m |

        ## 6.4 Tightening sequence

        Tighten the bolts in a star pattern in three passes.
        """;

    public static TiktokenTokenizer O200k { get; } = TiktokenTokenizer.CreateForEncoding("o200k_base");

    /// <summary>Probability = sigmoid(score): a score of 2 reads as 0.88, -10 as 0.00005.</summary>
    public static Calibration Identity { get; } = new(1, 0);

    public static SourceDocument Document { get; } = new("CP200-MAN", "CP-200 manual", "fluid", ["employees"], Manual.ReplaceLineEndings("\n"));

    public static IndexedChunk Section(string heading)
    {
        var text = Document.Text;
        var start = text.IndexOf(heading, StringComparison.Ordinal);
        var end = text.IndexOf("\n## ", start + 1, StringComparison.Ordinal) is var next and > 0 ? next : text.Length;
        return Indexed(new Chunk(Document.Id, start, end, text[start..end]) { Header = "CP-200 manual › " + heading.TrimStart('#', ' ') });
    }

    /// <summary>The table's data rows as a table-rows chunk: the span holds the Markdown rows, the text names the columns.</summary>
    public static IndexedChunk TableRows()
    {
        var text = Document.Text;
        var start = text.IndexOf("| M12", StringComparison.Ordinal);
        var end = text.IndexOf("N·m |", text.IndexOf("| M16", StringComparison.Ordinal), StringComparison.Ordinal) + 5;
        return Indexed(new Chunk(Document.Id, start, end, "Bolt size: M12; Torque: 85 N·m\nBolt size: M16; Torque: 210 N·m") { Kind = "table-rows", Header = "CP-200 manual › 6.3 Flange bolt torque" });
    }

    private static IndexedChunk Indexed(Chunk chunk) => new(0, chunk, Document, "manuals", "manual", ["CP-200"], null, null);

    public static ContextAssembler Assembler(ContextOptions? options = null) => new(Identity, O200k, options);

    public static IChatClient Scripted() => new ScriptedChatClient([ExtractiveScript.Rule], new ScriptedChatClientOptions { ChunkDelay = TimeSpan.Zero, WordsPerChunk = 2 });

    public static GroundedAnswerGenerator Generator(IChatClient chat, params Hit[] hits) => new(new FixedRetriever(hits), Assembler(), chat, O200k);
}

internal sealed class FixedRetriever(params Hit[] hits) : IRetriever
{
    public string Name => "fixed";

    public Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(RetrievalResult.Of(hits));
}

/// <summary>Counts calls and answers with a fixed reply.</summary>
internal sealed class CountingChatClient(string reply) : IChatClient
{
    public int Calls { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls++;
        await Task.CompletedTask;
        yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
