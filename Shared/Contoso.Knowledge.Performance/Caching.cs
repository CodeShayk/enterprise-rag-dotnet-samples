using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Contoso.Corpus;
using Contoso.Knowledge.Generation;
using Microsoft.Extensions.Caching.Hybrid;

namespace Contoso.Knowledge.Performance;

/// <summary>
/// The keys of the assistant's caches. Each key holds everything that decides the cached value: a retrieval result
/// depends on who may read what and on the index; an answer depends on the evidence the model read, the prompt and the
/// model, so keying it by the evidence makes a re-indexed document or another person's access a different key.
/// </summary>
public static partial class CacheKeys
{
    /// <summary>A question as it's compared: lowercase, single spaces, no trailing punctuation.</summary>
    public static string Normalize(string question) =>
        Spaces().Replace(question.Trim().TrimEnd('?', '.', '!').ToLower(CultureInfo.InvariantCulture), " ");

    /// <summary>Retrieval results: the asker's tenant and groups, the index version and the question.</summary>
    public static string Retrieval(Person asker, string indexVersion, string question) =>
        Hash("retrieval", asker.Tenant, string.Join(',', asker.Groups.Order(StringComparer.Ordinal)), indexVersion, Normalize(question));

    /// <summary>An answer: the prompt version, the model, the question and every evidence segment the model read.</summary>
    public static string Answer(string promptVersion, string model, string question, IReadOnlyList<EvidenceBlock> evidence) =>
        Hash("answer", promptVersion, model, Normalize(question), Fingerprint(evidence));

    /// <summary>What a model was shown, as text: each block's document, segments and the hash of their text.</summary>
    public static string Fingerprint(IReadOnlyList<EvidenceBlock> evidence) =>
        string.Join(';', evidence.Select(b => $"{b.DocumentId}:{string.Join(',', b.Segments.Select(s => $"{s.Start}-{s.End}"))}:{Hash(b.Text)[..12]}"));

    /// <summary>One tag per document an answer's evidence came from, for invalidation when it's re-indexed or erased.</summary>
    public static IReadOnlyList<string> DocumentTags(IReadOnlyList<EvidenceBlock> evidence) =>
        [.. evidence.Select(b => $"doc:{b.DocumentId}").Distinct(StringComparer.Ordinal)];

    public static string Hash(params string[] parts) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts))));

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>
/// Answers on HybridCache: memory first, then the distributed tier when one is registered, one computation per key
/// at a time, and a tag for every document behind an answer. The evidence is retrieved before the lookup, under the
/// asker's permissions, so only the model call is cached.
/// </summary>
public sealed class AnswerCache(HybridCache cache, string promptVersion, string model, TimeSpan? lifetime = null)
{
    private readonly HybridCacheEntryOptions _options = new() { Expiration = lifetime ?? TimeSpan.FromDays(1), LocalCacheExpiration = TimeSpan.FromMinutes(10) };

    public async ValueTask<(Answer Answer, bool Hit)> GetOrAnswerAsync(
        string question, IReadOnlyList<EvidenceBlock> evidence, Func<CancellationToken, ValueTask<Answer>> answer, CancellationToken cancellationToken = default)
    {
        var computed = false;
        var result = await cache.GetOrCreateAsync(
            CacheKeys.Answer(promptVersion, model, question, evidence),
            async token =>
            {
                computed = true;
                return await answer(token);
            },
            _options, CacheKeys.DocumentTags(evidence), cancellationToken);
        return (result, !computed);
    }

    /// <summary>Drops every answer whose evidence came from the document: on re-indexing, and on an erasure request.</summary>
    public ValueTask InvalidateDocumentAsync(string documentId, CancellationToken cancellationToken = default) =>
        cache.RemoveByTagAsync($"doc:{documentId}", cancellationToken);
}
