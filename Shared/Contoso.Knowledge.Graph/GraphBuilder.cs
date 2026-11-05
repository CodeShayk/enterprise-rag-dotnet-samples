using Contoso.Knowledge.Chunking;

namespace Contoso.Knowledge.Graph;

/// <summary>
/// Which extractors run: table rows, document-subject rules and, optionally, the schema-bound model, either on every
/// prose chunk or (<paramref name="Selective"/>) only in documents whose subject the rules can't find.
/// </summary>
public sealed record ExtractionOptions(bool Tables = true, bool Subject = true, ModelRelationExtractor? Model = null, bool Selective = false);

/// <summary>
/// Adds one document version to the graph. Every entity and relation gets a mention that points at the chunk it was
/// read from, so a citation can always go back to the text, and removing the version removes exactly what it added.
/// </summary>
public sealed class GraphBuilder(EntityRecognizer recognizer)
{
    public async Task<int> AddAsync(KnowledgeGraph graph, ChunkSource source, string version, IReadOnlyList<Chunk> chunks, ExtractionOptions options, CancellationToken cancellationToken = default)
    {
        Mention At(int start, int end, string method, double confidence)
        {
            var chunk = chunks.FirstOrDefault(c => c.Start <= start && start < c.End) ?? chunks.FirstOrDefault(c => c.Start < end && start < c.End);
            return chunk is null ? new Mention(source.Id, version, start, end, method, confidence) : new Mention(source.Id, version, chunk.Start, chunk.End, method, confidence);
        }

        foreach (var c in chunks)
        {
            foreach (var e in recognizer.Find(source.Text[c.Start..c.End], c.Start)) graph.AddNode(e.Id, e.Type, e.Name, At(e.Start, e.End, e.Method, 1.0));
        }

        var triples = new List<Triple>();
        if (options.Tables) triples.AddRange(RuleExtractor.FromTables(source, recognizer));
        if (options.Subject) triples.AddRange(RuleExtractor.FromSubject(source, recognizer));
        var subject = RuleExtractor.Subject(source, recognizer);
        if (options.Model is { } model && !(options.Selective && subject is not null))
        {
            foreach (var c in chunks.Where(c => c.Kind != "table-rows"))
            {
                var entities = recognizer.Find(source.Text[c.Start..c.End], c.Start).ToList();
                if (subject is not null) entities.Add(subject);
                triples.AddRange(await model.ExtractAsync(source, c, entities, cancellationToken));
            }
        }

        foreach (var t in triples)
        {
            var mention = At(t.Start, t.End, t.Method, t.Confidence);
            graph.AddNode(t.From.Id, t.From.Type, t.From.Name, mention);
            graph.AddNode(t.To.Id, t.To.Type, t.To.Name, mention);
            graph.AddEdge(t.From.Id, t.Relation, t.To.Id, mention);
        }

        return triples.Count;
    }
}
