using System.Runtime.CompilerServices;
using Contoso.Corpus;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ch03.Knowledge;

/// <summary>The evidence for one answer, and the answer itself as it streams in.</summary>
public sealed record KnowledgeAnswer(IReadOnlyList<RetrievedChunk> Sources, IAsyncEnumerable<ChatResponseUpdate> Updates);

/// <summary>Retrieve, augment, generate: Chapter 1's pipeline on real building blocks.</summary>
public sealed class KnowledgeAssistant(
    KnowledgeIndex index,
    [FromKeyedServices(KnowledgeModels.Answer)] IChatClient chat,
    IOptions<KnowledgeOptions> options)
{
    public const string NoEvidence = "I couldn't find this in any document you can read, so I won't guess.";

    public async Task<KnowledgeAnswer> AskAsync(string question, Person asker, CancellationToken cancellationToken = default)
    {
        // Retrieve: the top chunks this asker may read.
        var evidence = await index.SearchAsync(question, asker, options.Value.Top, cancellationToken);
        if (evidence.Count == 0)
        {
            return new KnowledgeAnswer(evidence, Say(NoEvidence));
        }

        // Augment and generate. The caller reads the sources first and then the streamed answer.
        var messages = PromptBuilder.Build(question, evidence);
        var chatOptions = new ChatOptions { Temperature = 0f, MaxOutputTokens = options.Value.MaxAnswerTokens };
        return new KnowledgeAnswer(evidence, chat.GetStreamingResponseAsync(messages, chatOptions, cancellationToken));
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Say(string text, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield return new ChatResponseUpdate(ChatRole.Assistant, text);
    }
}
