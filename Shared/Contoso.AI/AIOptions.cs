namespace Contoso.AI;

/// <summary>Which model sits behind the assistant's <c>IChatClient</c>.</summary>
public enum AIProvider
{
    /// <summary>A deterministic, offline stand-in that follows scripts. The default; needs no model or key.</summary>
    Scripted,

    /// <summary>A local model served by Ollama.</summary>
    Ollama,

    /// <summary>Azure OpenAI in Microsoft Foundry, authenticated with Microsoft Entra ID.</summary>
    AzureOpenAI,

    /// <summary>OpenAI's public API.</summary>
    OpenAI,
}

/// <summary>Model settings, read from the <c>AI</c> configuration section.</summary>
public sealed class AIOptions
{
    public const string SectionName = "AI";

    public AIProvider Provider { get; set; } = AIProvider.Scripted;

    /// <summary>Azure OpenAI resource endpoint, for example <c>https://contoso.openai.azure.com/</c>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Azure OpenAI deployment name or OpenAI model name.</summary>
    public string? Model { get; set; }

    /// <summary>OpenAI API key. Keep it in User Secrets or a secret store, never in appsettings.json.</summary>
    public string? ApiKey { get; set; }

    public string OllamaEndpoint { get; set; } = "http://localhost:11434";

    public string OllamaModel { get; set; } = "llama3.2";

    /// <summary>The pause between streamed chunks from the scripted model; zero in tests.</summary>
    public int ScriptedChunkDelayMilliseconds { get; set; } = 25;

    /// <summary>How long one model call may take. Local models on a CPU can need minutes for a long prompt.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>The embedding model, from the <c>AI:Embeddings</c> section.</summary>
    public EmbeddingOptions Embeddings { get; set; } = new();

    /// <summary>A short description of the configured model, for logs and console output.</summary>
    public string Describe() => Provider switch
    {
        AIProvider.Scripted => "scripted (a deterministic stand-in, not a language model)",
        AIProvider.Ollama => $"Ollama {OllamaModel} at {OllamaEndpoint}",
        AIProvider.AzureOpenAI => $"Azure OpenAI deployment {Model}",
        _ => $"OpenAI {Model}",
    };
}
