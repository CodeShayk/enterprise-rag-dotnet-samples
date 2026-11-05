namespace Contoso.AI;

/// <summary>Which model sits behind the assistant's <c>IEmbeddingGenerator</c>.</summary>
public enum EmbeddingProvider
{
    /// <summary>A deterministic, offline stand-in that hashes words and word fragments. The default.</summary>
    Hashing,

    /// <summary>A local embedding model served by Ollama.</summary>
    Ollama,

    /// <summary>An embedding deployment in Azure OpenAI, authenticated with Microsoft Entra ID.</summary>
    AzureOpenAI,

    /// <summary>OpenAI's public API.</summary>
    OpenAI,
}

/// <summary>Embedding settings, read from the <c>AI:Embeddings</c> configuration section.</summary>
public sealed class EmbeddingOptions
{
    public EmbeddingProvider Provider { get; set; } = EmbeddingProvider.Hashing;

    /// <summary>The Ollama model, Azure OpenAI deployment or OpenAI model name.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// The vector length. It's part of the index's schema: a different model with a different
    /// length needs a new collection, and its vectors can't be compared with the old ones.
    /// </summary>
    public int Dimensions { get; set; } = HashingEmbeddingGenerator.DefaultDimensions;

    public string Describe() => Provider switch
    {
        EmbeddingProvider.Hashing => $"hashing, {Dimensions} dimensions (a deterministic stand-in, not a language model)",
        EmbeddingProvider.Ollama => $"Ollama {Model}",
        EmbeddingProvider.AzureOpenAI => $"Azure OpenAI deployment {Model}, {Dimensions} dimensions",
        _ => $"OpenAI {Model}, {Dimensions} dimensions",
    };
}
