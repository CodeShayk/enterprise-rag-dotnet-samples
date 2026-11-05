using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ch03.Knowledge;

/// <summary>Which models the assistant is using, for logs and console output.</summary>
public sealed record ModelDescription(string Chat, string Embeddings);

/// <summary>Loads and indexes the Markdown corpus once, when the host starts.</summary>
public sealed partial class KnowledgeIndexer(
    KnowledgeIndex index, IOptions<KnowledgeOptions> options, ModelDescription models, ILogger<KnowledgeIndexer> logger)
    : IHostedService
{
    public IndexingReport? Report { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var documents = MarkdownLoader.LoadFolder(options.Value.ContentPath);
        Report = await index.IndexAsync(documents, cancellationToken);
        LogIndexed(Report.Documents, Report.Chunks, Report.Elapsed.TotalMilliseconds, models.Embeddings);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(LogLevel.Information, "Indexed {Documents} documents as {Chunks} chunks in {Milliseconds:F0} ms with {Embeddings}.")]
    private partial void LogIndexed(int documents, int chunks, double milliseconds, string embeddings);
}
