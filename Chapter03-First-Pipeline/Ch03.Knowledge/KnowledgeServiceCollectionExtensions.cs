using Ch03.Knowledge;
using Contoso.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the walking skeleton: models, middleware, vector store, index and assistant.</summary>
public static class KnowledgeServiceCollectionExtensions
{
    public static IServiceCollection AddContosoKnowledge(this IServiceCollection services, IConfiguration configuration)
    {
        var ai = AIClientFactory.ReadOptions(configuration);
        services.AddOptions<KnowledgeOptions>().Bind(configuration.GetSection(KnowledgeOptions.SectionName));

        services.AddSingleton<Tokenizer>(_ => TiktokenTokenizer.CreateForEncoding("o200k_base"));
        services.AddSingleton<ChatMeasurements>();
        services.AddDistributedMemoryCache();

        // The answer model, keyed so that a query-rewriting model or a judge can sit beside it.
        // The first Use is the outermost layer; the model sits at the bottom.
        services.AddKeyedChatClient(KnowledgeModels.Answer, _ => AIClientFactory.CreateChatClient(ai))
            .UseLogging()
            .UseDistributedCache()
            .UseOpenTelemetry(sourceName: KnowledgeTelemetry.SourceName)
            .Use((inner, sp) => new MeasuringChatClient(
                inner, sp.GetRequiredService<ChatMeasurements>(), sp.GetRequiredService<Tokenizer>()));

        services.AddEmbeddingGenerator(_ => AIClientFactory.CreateEmbeddingGenerator(ai))
            .UseOpenTelemetry(sourceName: KnowledgeTelemetry.SourceName);

        services.AddInMemoryVectorStore();
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<KnowledgeOptions>>().Value;
            return new FixedSizeChunker(sp.GetRequiredService<Tokenizer>(), options.ChunkTokens, options.ChunkOverlapTokens);
        });
        services.AddSingleton<KnowledgeIndex>();
        services.AddSingleton<KnowledgeAssistant>();

        // Index the Markdown corpus before the host starts serving.
        services.AddSingleton<KnowledgeIndexer>();
        services.AddHostedService(sp => sp.GetRequiredService<KnowledgeIndexer>());

        services.AddSingleton(new ModelDescription(ai.Describe(), ai.Embeddings.Describe()));
        return services;
    }
}
