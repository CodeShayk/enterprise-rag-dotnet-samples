using System.Text.Json;
using Contoso.Corpus;
using Contoso.Knowledge.Evaluation;
using Contoso.Knowledge.Governance;
using Contoso.Knowledge.Graph;
using Contoso.Knowledge.Retrieval;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace Ch23.Governance.Cli;

// Every place the assistant keeps source text or something derived from it, each able to count its copies of a
// document, erase them and count again. A source store erases only the document it was asked about: another document
// that contains a probe string is reported (the count after stays above zero) for a person to decide, never deleted.

file static class Probe
{
    public static bool In(string text, ErasureRequest request) => request.Probes.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
}

public sealed class DocumentStore(IReadOnlyDictionary<string, SourceDocument> documents) : IErasableStore
{
    private readonly Dictionary<string, SourceDocument> _documents = new(documents);

    public string Name => "source documents";

    public Task<int> CountAsync(ErasureRequest r, CancellationToken cancellationToken = default) =>
        Task.FromResult(_documents.Values.Count(d => d.Id == r.DocumentId || Probe.In(d.Text, r)));

    public Task<int> EraseAsync(ErasureRequest r, CancellationToken cancellationToken = default) => Task.FromResult(_documents.Remove(r.DocumentId) ? 1 : 0);
}

/// <summary>Lucene over the chunks; copies are the document's chunks and any chunk a probe query finds with the probe in it.</summary>
public sealed class KeywordIndexStore(IReadOnlyList<IndexedChunk> chunks) : IErasableStore
{
    private List<IndexedChunk> _chunks = [.. chunks];
    private LuceneRetriever _index = new("keyword", chunks, ContosoAnalyzers.Identifiers());

    public string Name => "keyword index (chunks)";

    public async Task<int> CountAsync(ErasureRequest r, CancellationToken cancellationToken = default)
    {
        var found = _chunks.Where(c => c.Document.Id == r.DocumentId).Select(c => c.Ordinal).ToHashSet();
        foreach (var probe in r.Probes)
        {
            var hits = (await _index.RetrieveAsync(new RetrievalRequest(probe, Lab.Everyone, 20), cancellationToken)).Hits;
            found.UnionWith(hits.Where(h => Probe.In(h.Chunk.Chunk.Text, r)).Select(h => h.Chunk.Ordinal));
        }

        return found.Count;
    }

    public Task<int> EraseAsync(ErasureRequest r, CancellationToken cancellationToken = default)
    {
        var before = _chunks.Count;
        _chunks = [.. _chunks.Where(c => c.Document.Id != r.DocumentId).Select((c, i) => c with { Ordinal = i })];
        _index = new LuceneRetriever("keyword", _chunks, ContosoAnalyzers.Identifiers());
        return Task.FromResult(before - _chunks.Count);
    }
}

/// <summary>One vector per chunk, keyed by the chunk and labeled with its document.</summary>
public sealed class VectorIndexStore(Dictionary<int, (string Document, ReadOnlyMemory<float> Vector)> vectors) : IErasableStore
{
    public string Name => "vector index";

    public Task<int> CountAsync(ErasureRequest r, CancellationToken cancellationToken = default) => Task.FromResult(vectors.Values.Count(v => v.Document == r.DocumentId));

    public Task<int> EraseAsync(ErasureRequest r, CancellationToken cancellationToken = default)
    {
        var keys = vectors.Where(v => v.Value.Document == r.DocumentId).Select(v => v.Key).ToList();
        foreach (var key in keys) vectors.Remove(key);
        return Task.FromResult(keys.Count);
    }
}

/// <summary>Chapter 19's graph: every entity and relation mention points at a document version, and removing the version removes them.</summary>
public sealed class GraphStore(KnowledgeGraph graph, IReadOnlyDictionary<string, string> versions) : IErasableStore
{
    public string Name => "knowledge graph (mentions)";

    public Task<int> CountAsync(ErasureRequest r, CancellationToken cancellationToken = default) => Task.FromResult(
        graph.Nodes.Sum(n => n.Mentions.Count(m => m.DocumentId == r.DocumentId)) + graph.Edges.Sum(e => e.Mentions.Count(m => m.DocumentId == r.DocumentId)));

    public Task<int> EraseAsync(ErasureRequest r, CancellationToken cancellationToken = default) =>
        Task.FromResult(versions.TryGetValue(r.DocumentId, out var version) ? graph.RemoveVersion(r.DocumentId, version).Mentions : 0);
}

/// <summary>
/// Answers cached on every node of the service, tagged with each evidence document. Erasure is broadcast to every node:
/// a tag removed on one node never reaches another node's memory (Chapter 21).
/// </summary>
public sealed class AnswerCacheStore(IReadOnlyList<HybridCache> nodes, IReadOnlyList<ContentEntry> answers) : IErasableStore
{
    private const string Recomputed = "(recomputed)";

    public string Name => $"answer cache, {nodes.Count} nodes";

    public static async Task<AnswerCacheStore> CreateAsync(int count, IReadOnlyList<ContentEntry> answers)
    {
        var nodes = Enumerable.Range(0, count).Select(_ => new ServiceCollection().AddHybridCache().Services.BuildServiceProvider().GetRequiredService<HybridCache>()).ToList();
        foreach (var node in nodes)
        {
            foreach (var a in answers) await node.SetAsync(a.RequestId, a.Answer, tags: [.. a.Documents.Select(d => "doc:" + d)]);
        }

        return new AnswerCacheStore(nodes, answers);
    }

    private IEnumerable<ContentEntry> Copies(ErasureRequest r) => answers.Where(a => a.Documents.Contains(r.DocumentId) || Probe.In(a.Answer, r));

    public async Task<int> CountAsync(ErasureRequest r, CancellationToken cancellationToken = default)
    {
        var served = 0;
        foreach (var node in nodes)
        {
            foreach (var a in Copies(r))
            {
                if (await node.GetOrCreateAsync(a.RequestId, _ => ValueTask.FromResult(Recomputed), cancellationToken: cancellationToken) != Recomputed) served++;
            }
        }

        return served;
    }

    public async Task<int> EraseAsync(ErasureRequest r, CancellationToken cancellationToken = default)
    {
        var copies = Copies(r).ToList();
        foreach (var node in nodes)
        {
            await node.RemoveByTagAsync("doc:" + r.DocumentId, cancellationToken);
            foreach (var a in copies) await node.RemoveAsync(a.RequestId, cancellationToken);
        }

        return copies.Count * nodes.Count;
    }
}

/// <summary>Captured questions and answers (Chapter 20's capture modes): copies when they used the document's text or quote a probe.</summary>
public sealed class ContentStore(string path) : IErasableStore
{
    public string Name => "captured questions and answers";

    private IEnumerable<ContentEntry> Entries() => File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<ContentEntry>(l, ProvenanceCommand.Json)!);

    private static bool Holds(ContentEntry e, ErasureRequest r) => e.Documents.Contains(r.DocumentId) || Probe.In(e.Question + "\n" + e.Answer, r);

    public Task<int> CountAsync(ErasureRequest r, CancellationToken cancellationToken = default) => Task.FromResult(Entries().Count(e => Holds(e, r)));

    public async Task<int> EraseAsync(ErasureRequest r, CancellationToken cancellationToken = default)
    {
        var (keep, erased) = (Entries().Where(e => !Holds(e, r)).ToList(), Entries().Count(e => Holds(e, r)));
        await File.WriteAllLinesAsync(path, keep.Select(e => JsonSerializer.Serialize(e, ProvenanceCommand.Json)), cancellationToken);
        return erased;
    }
}

/// <summary>Recorded model calls kept as test fixtures: each prompt carries its evidence, so they are copies too.</summary>
public sealed class RecordingStore(string path) : IErasableStore
{
    public string Name => "recorded model calls";

    private static bool Holds(string line, ErasureRequest r) => line.Contains(r.DocumentId, StringComparison.Ordinal) || Probe.In(line, r);

    public Task<int> CountAsync(ErasureRequest r, CancellationToken cancellationToken = default) => Task.FromResult(File.ReadLines(path).Count(l => Holds(l, r)));

    public async Task<int> EraseAsync(ErasureRequest r, CancellationToken cancellationToken = default)
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        await File.WriteAllLinesAsync(path, lines.Where(l => !Holds(l, r)), cancellationToken);
        return lines.Count(l => Holds(l, r));
    }
}

/// <summary>Golden-set questions whose labeled spans come from the document or quote a probe: retired, not edited.</summary>
public sealed class GoldenSetStore(IReadOnlyList<GoldenQuestion> questions) : IErasableStore
{
    private readonly List<GoldenQuestion> _active = [.. questions];

    public List<string> Retired { get; } = [];

    public string Name => "golden set";

    private static bool Holds(GoldenQuestion q, ErasureRequest r) => q.Relevant.Any(s => s.Doc == r.DocumentId || Probe.In(s.Text, r)) || Probe.In(q.Question, r);

    public Task<int> CountAsync(ErasureRequest r, CancellationToken cancellationToken = default) => Task.FromResult(_active.Count(q => Holds(q, r)));

    public Task<int> EraseAsync(ErasureRequest r, CancellationToken cancellationToken = default)
    {
        var retired = _active.Where(q => Holds(q, r)).ToList();
        Retired.AddRange(retired.Select(q => q.Id));
        _active.RemoveAll(q => Holds(q, r));
        return Task.FromResult(retired.Count);
    }
}

/// <summary>
/// The provenance log holds identifiers and hashes, never text, so it has nothing to erase; a probe in it would mean
/// text leaked into the chain. Records that cite the document stay as the audit trail.
/// </summary>
public sealed class ProvenanceStore(string path) : IErasableStore
{
    public string Name => "provenance log (text)";

    public int Citing(string documentId) => new ProvenanceLog(path).Read().Count(r => r.Evidence.Any(e => e.DocumentId == documentId));

    public Task<int> CountAsync(ErasureRequest r, CancellationToken cancellationToken = default) => Task.FromResult(File.ReadLines(path).Count(l => Probe.In(l, r)));

    public Task<int> EraseAsync(ErasureRequest r, CancellationToken cancellationToken = default) => Task.FromResult(0);
}
