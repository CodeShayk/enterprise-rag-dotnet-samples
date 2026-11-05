using Ch03.Knowledge;
using Contoso.Corpus;
using Microsoft.Extensions.DependencyInjection;

namespace Ch03.Tests;

public sealed class PromptSnapshotTests
{
    [Fact]
    public async Task The_prompt_for_Tomas_torque_question()
    {
        var model = new RecordingChatClient();
        await using var services = await TestServices.CreateAsync(chat: model);
        var assistant = services.GetRequiredService<KnowledgeAssistant>();

        var answer = await assistant.AskAsync(
            "What torque applies to the M12 flange bolts on P-201?", People.Tomas, TestContext.Current.CancellationToken);
        await foreach (var _ in answer.Updates)
        {
        }

        // The exact messages the model received. A change to the instructions, the chunking or the
        // retrieval shows up as a diff against the .verified.txt file, to review like any other code.
        var messages = Assert.Single(model.Calls);
        Snapshot.Match(string.Join("\n\n", messages.Select(m => $"{m.Role.Value}:\n{m.Text}")));
    }
}
