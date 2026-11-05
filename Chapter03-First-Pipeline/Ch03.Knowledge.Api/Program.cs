using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Ch03.Knowledge;
using Contoso.Corpus;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddContosoKnowledge(builder.Configuration);

var app = builder.Build();
app.MapDefaultEndpoints();

// One question in, one JSON answer out.
app.MapPost("/ask", async (AskRequest request, KnowledgeAssistant assistant, CancellationToken cancellationToken) =>
{
    if (People.Find(request.User) is not { } asker)
    {
        return Results.Problem($"Unknown user '{request.User}'.", statusCode: StatusCodes.Status400BadRequest);
    }

    var answer = await assistant.AskAsync(request.Question, asker, cancellationToken);
    var response = await answer.Updates.ToChatResponseAsync(cancellationToken);
    return Results.Ok(new AskResponse(response.Text, SourceView.From(answer.Sources)));
});

// The same answer as server-sent events: the sources, then the text as the model writes it, then done.
app.MapPost("/ask/stream", async (AskRequest request, KnowledgeAssistant assistant, CancellationToken cancellationToken) =>
{
    if (People.Find(request.User) is not { } asker)
    {
        return Results.Problem($"Unknown user '{request.User}'.", statusCode: StatusCodes.Status400BadRequest);
    }

    var answer = await assistant.AskAsync(request.Question, asker, cancellationToken);
    return TypedResults.ServerSentEvents(Events(answer, cancellationToken));
});

app.Run();

static async IAsyncEnumerable<SseItem<object>> Events(
    KnowledgeAnswer answer, [EnumeratorCancellation] CancellationToken cancellationToken)
{
    yield return new SseItem<object>(SourceView.From(answer.Sources), "sources");

    await foreach (var update in answer.Updates.WithCancellation(cancellationToken))
    {
        if (!string.IsNullOrEmpty(update.Text))
        {
            yield return new SseItem<object>(new Delta(update.Text), "delta");
        }
    }

    yield return new SseItem<object>(new Done(answer.Sources.Count), "done");
}

/// <summary>Who is asking and what. Chapter 22 replaces the user field with the signed-in identity.</summary>
public sealed record AskRequest(string User, string Question);

public sealed record AskResponse(string Answer, IReadOnlyList<SourceView> Sources);

/// <summary>One source as the client shows it: its number in the answer, its document and its score.</summary>
public sealed record SourceView(int N, string DocumentId, string Title, string ChunkId, double Score)
{
    public static IReadOnlyList<SourceView> From(IReadOnlyList<RetrievedChunk> sources) =>
        [.. sources.Select((s, i) => new SourceView(i + 1, s.DocumentId, s.Title, s.ChunkId, Math.Round(s.Score, 3)))];
}

public sealed record Delta(string Text);

public sealed record Done(int Sources);

/// <summary>Visible to WebApplicationFactory in the tests.</summary>
public partial class Program;
