using Ch04.Knowledge.Adapters;
using Ch04.Knowledge.Core;
using Ch04.Knowledge.Core.Ports;
using Contoso.AI;
using Contoso.Corpus;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML.Tokenizers;

namespace Ch04.Architecture.Cli;

/// <summary>The composition root: the only place that knows which adapter serves which port.</summary>
public static class Composition
{
    public static IServiceCollection AddKnowledgePipeline(this IServiceCollection services, IConfiguration configuration)
    {
        var pipeline = configuration.GetSection("Pipeline");
        var ai = AIClientFactory.ReadOptions(configuration);

        // Ingestion plane.
        services.AddSingleton<Tokenizer>(_ => TiktokenTokenizer.CreateForEncoding("o200k_base"));
        services.AddSingleton<IDocumentSource>(_ => new MarkdownFolderSource(pipeline["ContentPath"] ?? MarkdownCorpus.Folder));
        services.AddSingleton<IChunker>(sp => new FixedSizeChunker(
            sp.GetRequiredService<Tokenizer>(), pipeline.GetValue("ChunkTokens", 100), pipeline.GetValue("ChunkOverlapTokens", 20)));

        // One index adapter serves both planes, so the writer and the retriever always agree.
        switch (pipeline["Retriever"])
        {
            case "Vector":
                services.AddEmbeddingGenerator(_ => AIClientFactory.CreateEmbeddingGenerator(ai));
                services.AddInMemoryVectorStore();
                services.AddSingleton<VectorIndex>();
                services.AddSingleton<IIndexWriter>(sp => sp.GetRequiredService<VectorIndex>());
                services.AddSingleton<IRetriever>(sp => sp.GetRequiredService<VectorIndex>());
                break;
            case "Keyword":
                services.AddSingleton<KeywordIndex>();
                services.AddSingleton<IIndexWriter>(sp => sp.GetRequiredService<KeywordIndex>());
                services.AddSingleton<IRetriever>(sp => sp.GetRequiredService<KeywordIndex>());
                break;
            default:
                throw new InvalidOperationException($"Pipeline:Retriever must be Vector or Keyword, not '{pipeline["Retriever"]}'.");
        }

        // Query plane.
        services.AddSingleton<IReranker, PassThroughReranker>();
        services.AddChatClient(_ => AIClientFactory.CreateChatClient(ai));
        services.AddSingleton<IAnswerGenerator, ChatAnswerGenerator>();
        services.AddSingleton(new PipelineOptions
        {
            Candidates = pipeline.GetValue("Candidates", 20),
            Top = pipeline.GetValue("Top", 4),
        });

        services.AddSingleton<IngestionPipeline>();
        services.AddSingleton<RagPipeline>();
        return services;
    }

    /// <summary>Which adapter serves each port, for the console and the logs.</summary>
    public static IEnumerable<(string Port, string Adapter)> Describe(IServiceProvider services) =>
    [
        (nameof(IDocumentSource), services.GetRequiredService<IDocumentSource>().GetType().Name),
        (nameof(IChunker), services.GetRequiredService<IChunker>().GetType().Name),
        (nameof(IRetriever), services.GetRequiredService<IRetriever>().GetType().Name),
        (nameof(IReranker), services.GetRequiredService<IReranker>().GetType().Name),
        (nameof(IAnswerGenerator), $"{services.GetRequiredService<IAnswerGenerator>().GetType().Name} on " +
            (services.GetRequiredService<IChatClient>().GetService<ChatClientMetadata>()?.ProviderName
             ?? services.GetRequiredService<IChatClient>().GetType().Name)),
    ];
}
