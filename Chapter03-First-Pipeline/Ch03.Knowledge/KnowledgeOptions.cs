using Contoso.Corpus;

namespace Ch03.Knowledge;

/// <summary>The walking skeleton's settings, read from the <c>Knowledge</c> configuration section.</summary>
public sealed class KnowledgeOptions
{
    public const string SectionName = "Knowledge";

    /// <summary>The folder of Markdown files to index at startup.</summary>
    public string ContentPath { get; set; } = MarkdownCorpus.Folder;

    /// <summary>The size of each chunk, in o200k_base tokens.</summary>
    public int ChunkTokens { get; set; } = 100;

    /// <summary>How many tokens each chunk repeats from the end of the previous one.</summary>
    public int ChunkOverlapTokens { get; set; } = 20;

    /// <summary>How many chunks become evidence for one answer.</summary>
    public int Top { get; set; } = 4;

    /// <summary>The longest answer the model may write.</summary>
    public int MaxAnswerTokens { get; set; } = 400;
}

/// <summary>Service keys for the assistant's models. Later chapters add more beside the answer model.</summary>
public static class KnowledgeModels
{
    public const string Answer = "answer";
}

/// <summary>The name of the assistant's traces and metrics.</summary>
public static class KnowledgeTelemetry
{
    public const string SourceName = "Contoso.Knowledge";
}
