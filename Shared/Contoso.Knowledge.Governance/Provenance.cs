using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Contoso.Knowledge.Governance;

/// <summary>One block of evidence the model read: the document version, the range and a hash of the text itself.</summary>
public sealed record EvidenceRef(string DocumentId, string Version, int Start, int End, string TextSha);

/// <summary>One claim as shown: the document it cites (empty for a sentence without a source) and whether its quote was found.</summary>
public sealed record ClaimRef(string DocumentId, bool QuoteFound);

/// <summary>
/// What produced one answer, recorded when it was shown: who asked (a keyed pseudonym and the tenant), the question's
/// hash, the index version and retriever, every evidence block with its document version and text hash, the prompt
/// version, the model and its digest, and the outcome. Text is kept out on purpose: questions and answers live in a
/// content store with its own retention, so erasing them never breaks the chain.
/// </summary>
public sealed record AnswerProvenanceRecord
{
    public long Sequence { get; init; }

    public DateTimeOffset At { get; init; }

    public required string RequestId { get; init; }

    public required string Asker { get; init; }

    public required string Tenant { get; init; }

    public required string QuestionSha { get; init; }

    public required string IndexVersion { get; init; }

    public required string Retriever { get; init; }

    public required IReadOnlyList<EvidenceRef> Evidence { get; init; }

    public required string PromptVersion { get; init; }

    public required string Model { get; init; }

    public required string Status { get; init; }

    public required string AnswerSha { get; init; }

    public required IReadOnlyList<ClaimRef> Claims { get; init; }

    /// <summary>A hash of the effective runtime configuration (Chapter 24); records written without one leave it out.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Configuration { get; init; }

    public string PreviousHash { get; init; } = "";

    public string Hash { get; init; } = "";

    public static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>
/// An append-only log of provenance records in which each record carries the hash of the one before it, so a change
/// to any record, or a deleted one, breaks every hash after it. In production the file sits in storage that refuses
/// overwrites (an immutability policy), and the last hash is copied somewhere the log's writers can't reach. A log
/// opened on an existing file continues its chain from the last record, so a restarted service doesn't start a new one.
/// </summary>
public sealed class ProvenanceLog(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private (long Sequence, string Hash) _last = LastOf(path);

    public string Path { get; } = path;

    public AnswerProvenanceRecord Append(AnswerProvenanceRecord record)
    {
        var sealedRecord = record with { Sequence = _last.Sequence + 1, PreviousHash = _last.Hash, Hash = "" };
        sealedRecord = sealedRecord with { Hash = HashOf(sealedRecord) };
        File.AppendAllText(Path, JsonSerializer.Serialize(sealedRecord, Json) + "\n");
        _last = (sealedRecord.Sequence, sealedRecord.Hash);
        return sealedRecord;
    }

    public IEnumerable<AnswerProvenanceRecord> Read() =>
        File.ReadLines(Path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<AnswerProvenanceRecord>(l, Json)!);

    /// <summary>Recomputes every hash and link; returns the first record that doesn't match, or null when the chain holds.</summary>
    public long? FirstBroken()
    {
        var previous = "";
        foreach (var record in Read())
        {
            if (record.PreviousHash != previous || HashOf(record with { Hash = "" }) != record.Hash) return record.Sequence;
            previous = record.Hash;
        }

        return null;
    }

    private static string HashOf(AnswerProvenanceRecord record) => AnswerProvenanceRecord.Sha(JsonSerializer.Serialize(record, Json));

    private static (long Sequence, string Hash) LastOf(string path)
    {
        if (!File.Exists(path)) return (0, "");
        var last = File.ReadLines(path).LastOrDefault(l => l.Length > 0);
        return last is null ? (0, "") : JsonSerializer.Deserialize<AnswerProvenanceRecord>(last, Json) is { } record ? (record.Sequence, record.Hash) : (0, "");
    }
}
