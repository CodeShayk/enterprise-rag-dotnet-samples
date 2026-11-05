using Microsoft.ML.Tokenizers;

namespace Ch02.Fundamentals;

/// <summary>Counts and splits text with one tiktoken encoding, such as o200k_base or cl100k_base.</summary>
public sealed class TokenCounter(string encoding)
{
    private readonly Tokenizer _tokenizer = TiktokenTokenizer.CreateForEncoding(encoding);

    public string Encoding { get; } = encoding;

    public int Count(string text) => _tokenizer.CountTokens(text);

    /// <summary>The text of each token, in order.</summary>
    public IReadOnlyList<string> Split(string text) =>
        [.. _tokenizer.EncodeToTokens(text, out _).Select(token => token.Value)];
}
