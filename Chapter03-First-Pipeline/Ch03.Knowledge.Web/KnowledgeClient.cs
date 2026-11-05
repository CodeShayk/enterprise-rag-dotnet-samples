using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;

namespace Ch03.Knowledge.Web;

/// <summary>One source as the API describes it.</summary>
public sealed record SourceView(int N, string DocumentId, string Title, string ChunkId, double Score);

/// <summary>A piece of the answer.</summary>
public sealed record Delta(string Text);

/// <summary>Calls the API's streaming endpoint and hands back its events as they arrive.</summary>
public sealed class KnowledgeClient(HttpClient http)
{
    public async IAsyncEnumerable<SseItem<string>> AskAsync(
        string user, string question, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ask/stream")
        {
            Content = JsonContent.Create(new { user, question }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        // ResponseHeadersRead returns as soon as the headers arrive; without it, HttpClient
        // would buffer the whole answer before handing any of it over.
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var item in SseParser.Create(body).EnumerateAsync(cancellationToken))
        {
            yield return item;
        }
    }
}
