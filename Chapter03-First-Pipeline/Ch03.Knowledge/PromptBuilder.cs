using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Ch03.Knowledge;

/// <summary>Turns a question and its evidence into the messages the model sees: Chapter 1's prompt, unchanged.</summary>
public static partial class PromptBuilder
{
    public const string Instructions =
        "You are the Contoso Knowledge Assistant. Answer only from the numbered sources. " +
        "Cite each source you use as [n]. If the sources don't answer the question, say that you don't know.";

    public static List<ChatMessage> Build(string question, IReadOnlyList<RetrievedChunk> evidence)
    {
        var prompt = new StringBuilder("Sources:\n");
        for (var i = 0; i < evidence.Count; i++)
        {
            // A blank line ends a source, so the chunk's own blank lines are collapsed.
            var text = BlankLines().Replace(evidence[i].Text, "\n");
            prompt.Append($"[{i + 1}] {evidence[i].Title} ({evidence[i].DocumentId})\n{text}\n\n");
        }

        prompt.Append($"Question: {question}");

        return [new ChatMessage(ChatRole.System, Instructions), new ChatMessage(ChatRole.User, prompt.ToString())];
    }

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex BlankLines();
}
