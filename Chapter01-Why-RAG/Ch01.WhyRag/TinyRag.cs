using System.Text;
using Contoso.Corpus;
using Microsoft.Extensions.AI;

namespace Ch01.WhyRag;

/// <summary>An answer, the sources it was grounded in, and whether the pipeline declined to answer.</summary>
public sealed record RagAnswer(string Text, IReadOnlyList<Hit> Sources, bool Abstained);

/// <summary>Retrieve, augment, generate: the whole idea of retrieval-augmented generation in one class.</summary>
public sealed class TinyRag(IChatClient chat, KeywordRetriever retriever)
{
    public const string Instructions =
        "You are the Contoso Knowledge Assistant. Answer only from the numbered sources. " +
        "Cite each source you use as [n]. If the sources don't answer the question, say that you don't know.";

    public const string NoEvidence = "I couldn't find this in any document you can read, so I won't guess.";

    public async Task<RagAnswer> AnswerAsync(string question, Person asker, CancellationToken cancellationToken = default)
    {
        // Retrieve: only documents the asker may read can become evidence.
        IReadOnlyList<Hit> hits = retriever.Retrieve(question, asker);
        if (hits.Count == 0)
        {
            return new RagAnswer(NoEvidence, hits, Abstained: true);
        }

        // Augment: number each source so the answer can cite it.
        var prompt = new StringBuilder("Sources:\n");
        for (var i = 0; i < hits.Count; i++)
        {
            prompt.Append($"[{i + 1}] {hits[i].Document.Title} ({hits[i].Document.Id})\n{hits[i].Document.Text}\n\n");
        }

        prompt.Append($"Question: {question}");

        // Generate: the model sees the instructions, the evidence and the question, nothing else.
        ChatResponse response = await chat.GetResponseAsync(
            [new ChatMessage(ChatRole.System, Instructions), new ChatMessage(ChatRole.User, prompt.ToString())],
            new ChatOptions { Temperature = 0f },
            cancellationToken);

        return new RagAnswer(response.Text, hits, Abstained: false);
    }

    /// <summary>The same question with no retrieval: whatever the model already knows.</summary>
    public async Task<string> AskModelOnlyAsync(string question, CancellationToken cancellationToken = default)
    {
        ChatResponse response = await chat.GetResponseAsync(question, new ChatOptions { Temperature = 0f }, cancellationToken);
        return response.Text;
    }
}
