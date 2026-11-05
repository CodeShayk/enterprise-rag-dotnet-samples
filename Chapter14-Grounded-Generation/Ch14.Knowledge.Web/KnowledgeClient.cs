using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;

namespace Ch14.Knowledge.Web;

// The API's contracts, as this client reads them.
public sealed record SourceView(string Id, string DocumentId, string Title, string? Section, double Probability, string Reference, string Text);

public sealed record SourcesView(IReadOnlyList<SourceView> Sources, IReadOnlyList<string> Degraded);

public sealed record Delta(string Text);

public sealed record ClaimView(string Text, string Source, string Quote, bool Verified, string? DocumentId, int Start, int End);

public sealed record AnswerView(
    string Status, string Text, IReadOnlyList<ClaimView> Claims, IReadOnlyList<SourceView> Sources, double Confidence,
    string PromptVersion, IReadOnlyList<string> Problems, IReadOnlyList<string> Degraded, double FirstTokenMs, double TotalMs);

/// <summary>Calls the API: questions as server-sent events, ratings as a POST.</summary>
public sealed class KnowledgeClient(HttpClient http)
{
    public async IAsyncEnumerable<SseItem<string>> AskAsync(
        string user, string question, string conversationId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ask/stream")
        {
            Content = JsonContent.Create(new { user, question, conversationId }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        // ResponseHeadersRead hands the stream over as soon as the headers arrive, so each event shows as it comes.
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var item in SseParser.Create(body).EnumerateAsync(cancellationToken))
        {
            yield return item;
        }
    }

    public async Task RateAsync(string user, string question, string rating, AnswerView answer, string conversationId, CancellationToken cancellationToken = default)
    {
        var feedback = new { user, question, rating, answer.PromptVersion, sources = answer.Sources.Select(s => s.Reference), conversationId };
        using var response = await http.PostAsJsonAsync("/feedback", feedback, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
