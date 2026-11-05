namespace Contoso.Knowledge.Embeddings;

/// <summary>How a model turns its token vectors into one vector for the text.</summary>
public enum Pooling
{
    /// <summary>The vector of the [CLS] token, the first in the sequence (BGE).</summary>
    Cls,

    /// <summary>The average of the token vectors, padding excluded (all-MiniLM, E5).</summary>
    Mean,
}

/// <summary>
/// Everything an embedding model needs besides its weights, read from its model card and configuration files:
/// dimensions, pooling, the prefixes queries and passages get, and the sequence limit. Guessing any of them
/// produces vectors that look fine and retrieve badly.
/// </summary>
public sealed record EmbeddingModelSpec(
    string Name, string ModelPath, string VocabPath, int Dimensions, Pooling Pooling, int MaxTokens,
    string QueryPrefix = "", string PassagePrefix = "", bool LowerCase = true);

/// <summary>
/// Finds the models <c>eng/get-models.py</c> downloads into <c>sample-code/models</c>, by walking up from the
/// application folder to the folder that holds <c>models/models.json</c>.
/// </summary>
public static class EmbeddingModels
{
    /// <summary>The instruction bge-small-en-v1.5's model card gives for short queries that look for passages.</summary>
    public const string BgeQueryInstruction = "Represent this sentence for searching relevant passages: ";

    public static string Root { get; } = FindRoot();

    /// <summary>
    /// bge-small-en-v1.5 (MIT): 384 dimensions, [CLS] pooling (1_Pooling/config.json), lowercase WordPiece, 512 tokens.
    /// Passages take no prefix; queries take the instruction, which version 1.5 makes optional.
    /// </summary>
    public static EmbeddingModelSpec BgeSmall(bool int8 = true, bool queryInstruction = true) => new(
        (int8 ? "bge-small-en-v1.5-int8" : "bge-small-en-v1.5-fp32") + (queryInstruction ? "" : "-no-instruction"),
        Find(int8 ? "model_int8.onnx" : "model.onnx", int8 ? "bge-small-en-v1.5-int8" : "bge-small-en-v1.5-fp32"),
        Find("vocab.txt", "bge-small-en-v1.5-vocab"),
        384, Pooling.Cls, 512, QueryPrefix: queryInstruction ? BgeQueryInstruction : "");

    private static string Find(string file, string model)
    {
        var path = Path.Combine(Root, "bge-small-en-v1.5", file);
        return File.Exists(path) ? path : throw new FileNotFoundException($"No {path}. Run: python eng/get-models.py {model}");
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "models");
            if (File.Exists(Path.Combine(candidate, "models.json"))) return candidate;
        }

        return Path.Combine(AppContext.BaseDirectory, "models");
    }
}
