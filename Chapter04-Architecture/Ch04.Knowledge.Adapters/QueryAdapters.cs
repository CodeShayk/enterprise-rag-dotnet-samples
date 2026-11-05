using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Ch04.Knowledge.Core;
using Ch04.Knowledge.Core.Ports;
using Microsoft.Extensions.AI;

namespace Ch04.Knowledge.Adapters;

/// <summary>Keeps the retriever's order. The slot exists so that Chapter 12's cross-encoder drops in without other changes.</summary>
public sealed class PassThroughReranker : IReranker
{
    public Task<IReadOnlyList<RetrievedChunk>> RerankAsync(
        string question, IReadOnlyList<RetrievedChunk> candidates, int top, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RetrievedChunk>>([.. candidates.Take(top)]);
}

/// <summary>Writes the answer with any <see cref="IChatClient"/>, from Chapter 1's prompt.</summary>
public sealed partial class ChatAnswerGenerator(IChatClient chat) : IAnswerGenerator
{
    public const string Instructions =
        "You are the Contoso Knowledge Assistant. Answer only from the numbered sources. " +
        "Cite each source you use as [n]. If the sources don't answer the question, say that you don't know.";

    public async IAsyncEnumerable<string> GenerateAsync(
        string question, IReadOnlyList<RetrievedChunk> evidence, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var options = new ChatOptions { Temperature = 0f, MaxOutputTokens = 400 };
        await foreach (var update in chat.GetStreamingResponseAsync(BuildMessages(question, evidence), options, cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return update.Text;
            }
        }
    }

    /// <summary>The messages the model sees; the budget calculator counts their tokens.</summary>
    public static List<ChatMessage> BuildMessages(string question, IReadOnlyList<RetrievedChunk> evidence)
    {
        var prompt = new StringBuilder("Sources:\n");
        for (var i = 0; i < evidence.Count; i++)
        {
            var chunk = evidence[i].Chunk;
            prompt.Append($"[{i + 1}] {chunk.Document.Title} ({chunk.Document.Id})\n{BlankLines().Replace(chunk.Text, "\n")}\n\n");
        }

        prompt.Append($"Question: {question}");
        return [new ChatMessage(ChatRole.System, Instructions), new ChatMessage(ChatRole.User, prompt.ToString())];
    }

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex BlankLines();
}
