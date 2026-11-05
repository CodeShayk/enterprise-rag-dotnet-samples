using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Ch14.Knowledge.Api;
using Contoso.Corpus;
using Contoso.Knowledge.Generation;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<KnowledgeStack>();
builder.Services.AddSingleton<Func<Task<GroundedAnswerGenerator>>>(sp => sp.GetRequiredService<KnowledgeStack>().GeneratorAsync);
builder.Services.AddSingleton<IConversationStore, InMemoryConversationStore>();
builder.Services.AddSingleton<FeedbackLog>();

var app = builder.Build();

// Start building the indexes now, so the first question doesn't wait for all of it.
_ = app.Services.GetRequiredService<Func<Task<GroundedAnswerGenerator>>>()();

// One question in, one typed answer out.
app.MapPost("/ask", async (AskRequest request, Func<Task<GroundedAnswerGenerator>> generator, IConversationStore conversations, CancellationToken cancellationToken) =>
{
    if (People.Find(request.User) is not { } asker) return Results.Problem($"Unknown user '{request.User}'.", statusCode: StatusCodes.Status400BadRequest);
    var history = request.ConversationId is { } id ? await conversations.GetAsync(id, cancellationToken) : [];
    Answer? answer = null;
    await foreach (var e in Ask(await generator(), request.Question, asker, history, cancellationToken))
    {
        if (e is AnswerReady ready) answer = ready.Answer;
    }

    if (request.ConversationId is { } conversation) await conversations.AppendAsync(conversation, new Turn(request.Question, answer!), cancellationToken);
    return Results.Ok(AnswerView.From(answer!));
});

// The same answer as server-sent events: the sources first, the text as it's written, then the validated answer.
app.MapPost("/ask/stream", async (AskRequest request, Func<Task<GroundedAnswerGenerator>> generator, IConversationStore conversations, CancellationToken cancellationToken) =>
{
    if (People.Find(request.User) is not { } asker) return Results.Problem($"Unknown user '{request.User}'.", statusCode: StatusCodes.Status400BadRequest);
    var history = request.ConversationId is { } id ? await conversations.GetAsync(id, cancellationToken) : [];
    return TypedResults.ServerSentEvents(Events(await generator(), request, asker, history, conversations, cancellationToken));
});

app.MapPost("/feedback", (FeedbackRequest feedback, FeedbackLog log) =>
{
    log.Add(feedback);
    return Results.Accepted();
});

app.Run();

static async IAsyncEnumerable<SseItem<object>> Events(
    GroundedAnswerGenerator generator, AskRequest request, Person asker, IReadOnlyList<Turn> history, IConversationStore conversations,
    [EnumeratorCancellation] CancellationToken cancellationToken)
{
    await foreach (var e in Ask(generator, request.Question, asker, history, cancellationToken))
    {
        switch (e)
        {
            case EvidenceReady ready:
                yield return new SseItem<object>(new SourcesView([.. ready.Evidence.Select(SourceView.From)], ready.Degraded), "sources");
                break;
            case AnswerDelta delta:
                yield return new SseItem<object>(new Delta(delta.Text), "delta");
                break;
            case AnswerReady done:
                if (request.ConversationId is { } id) await conversations.AppendAsync(id, new Turn(request.Question, done.Answer), cancellationToken);
                yield return new SseItem<object>(AnswerView.From(done.Answer), "answer");
                break;
        }
    }
}

// "Make that shorter" reworks the previous answer from its evidence; anything else is retrieved and answered.
static IAsyncEnumerable<AnswerEvent> Ask(GroundedAnswerGenerator generator, string question, Person asker, IReadOnlyList<Turn> history, CancellationToken cancellationToken) =>
    history.Count > 0 && FollowUps.IsRework(question)
        ? generator.StreamAsync(question, asker, history, history[^1].Answer.Evidence, cancellationToken)
        : generator.StreamAsync(question, asker, history, cancellationToken: cancellationToken);

/// <summary>Visible to WebApplicationFactory in the tests.</summary>
public partial class Program;
