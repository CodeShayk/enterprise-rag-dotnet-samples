namespace Contoso.Knowledge.Evaluation;

/// <summary>A chunk as the harness sees it: a range of characters in one document.</summary>
public sealed record ChunkSpan(string Doc, int Start, int End)
{
    public int Length => End - Start;
}

/// <summary>
/// Retrieval and context metrics over chunks that are judged against labeled spans, so the same
/// labels score any chunking. Overlap rule: a chunk is relevant to a span when it holds at least
/// half of the span's characters; its grade is the highest grade among such spans.
/// </summary>
public static class Metrics
{
    public const double OverlapRule = 0.5;

    public static int Grade(ChunkSpan chunk, IReadOnlyList<LocatedSpan> spans) =>
        spans.Where(span => span.Doc == chunk.Doc && Overlap(chunk, span) >= OverlapRule * span.Length)
             .Select(span => span.Grade)
             .DefaultIfEmpty(0)
             .Max();

    // Hit@k: is anything relevant in the top k?
    public static double HitAt(IReadOnlyList<int> grades, int k) => grades.Take(k).Any(g => g > 0) ? 1 : 0;

    // Precision@k: how much of the top k is relevant?
    public static double PrecisionAt(IReadOnlyList<int> grades, int k) => grades.Take(k).Count(g => g > 0) / (double)k;

    // Reciprocal rank: 1 / the position of the first relevant chunk, 0 when there is none in the top k.
    public static double ReciprocalRank(IReadOnlyList<int> grades, int k)
    {
        for (var i = 0; i < Math.Min(k, grades.Count); i++)
        {
            if (grades[i] > 0)
            {
                return 1.0 / (i + 1);
            }
        }

        return 0;
    }

    // Average precision at k: the mean of Precision@i at each relevant position, over min(k, relevant chunks that exist).
    public static double AveragePrecisionAt(IReadOnlyList<int> grades, int k, int relevantInCorpus)
    {
        double sum = 0;
        var found = 0;
        for (var i = 0; i < Math.Min(k, grades.Count); i++)
        {
            if (grades[i] > 0)
            {
                found++;
                sum += found / (double)(i + 1);
            }
        }

        return relevantInCorpus == 0 ? 0 : sum / Math.Min(k, relevantInCorpus);
    }

    // nDCG@k with linear gain (the grade itself, as trec_eval does) and a log2(rank + 1) discount.
    public static double NdcgAt(IReadOnlyList<int> grades, IReadOnlyList<int> idealGrades, int k)
    {
        static double Dcg(IEnumerable<int> g, int k) => g.Take(k).Select((grade, i) => grade / Math.Log2(i + 2)).Sum();
        var ideal = Dcg(idealGrades.OrderDescending(), k);
        return ideal == 0 ? 0 : Dcg(grades, k) / ideal;
    }

    /// <summary>Recall@k by fact: the share of the question's facts with a span that the top k chunks half-cover.</summary>
    public static double RecallAt(IReadOnlyList<ChunkSpan> topK, IReadOnlyList<LocatedSpan> spans) =>
        FactShare(spans, span => topK.Any(chunk => chunk.Doc == span.Doc && Overlap(chunk, span) >= OverlapRule * span.Length));

    /// <summary>Context recall: the share of facts with a span that lies entirely inside the evidence.</summary>
    public static double ContextRecall(IReadOnlyList<ChunkSpan> evidence, IReadOnlyList<LocatedSpan> spans) =>
        FactShare(spans, span => Covered(evidence, span) == span.Length);

    /// <summary>Rank-aware context precision, as RAGAS defines it: the mean of Precision@i over the relevant positions.</summary>
    public static double ContextPrecision(IReadOnlyList<int> grades)
    {
        double sum = 0;
        var relevant = 0;
        for (var i = 0; i < grades.Count; i++)
        {
            if (grades[i] > 0)
            {
                relevant++;
                sum += relevant / (double)(i + 1);
            }
        }

        return relevant == 0 ? 0 : sum / relevant;
    }

    /// <summary>Redundancy: the share of evidence characters that repeat characters already in the evidence.</summary>
    public static double Redundancy(IReadOnlyList<ChunkSpan> evidence)
    {
        var total = evidence.Sum(chunk => chunk.Length);
        var unique = evidence.GroupBy(chunk => chunk.Doc).Sum(doc => UnionLength(doc));
        return total == 0 ? 0 : 1 - unique / (double)total;
    }

    // The facts a question needs are its top-grade spans, grouped by fact; any span of a fact supplies it.
    private static double FactShare(IReadOnlyList<LocatedSpan> spans, Func<LocatedSpan, bool> satisfied)
    {
        if (spans.Count == 0)
        {
            return 0;
        }

        var top = spans.Max(s => s.Grade);
        var facts = spans.Where(s => s.Grade == top).GroupBy(s => s.Fact).ToList();
        return facts.Count(fact => fact.Any(satisfied)) / (double)facts.Count;
    }

    private static int Overlap(ChunkSpan chunk, LocatedSpan span) =>
        Math.Max(0, Math.Min(chunk.End, span.End) - Math.Max(chunk.Start, span.Start));

    // How many of the span's characters some evidence chunk from the same document covers.
    private static int Covered(IReadOnlyList<ChunkSpan> evidence, LocatedSpan span)
    {
        var ranges = evidence.Where(c => c.Doc == span.Doc)
            .Select(c => (Start: Math.Max(c.Start, span.Start), End: Math.Min(c.End, span.End)))
            .Where(r => r.End > r.Start);
        return UnionLength(ranges.Select(r => new ChunkSpan(span.Doc, r.Start, r.End)));
    }

    private static int UnionLength(IEnumerable<ChunkSpan> chunks)
    {
        var length = 0;
        var end = int.MinValue;
        foreach (var chunk in chunks.OrderBy(c => c.Start))
        {
            if (chunk.End <= end)
            {
                continue;
            }

            length += chunk.End - Math.Max(chunk.Start, end);
            end = chunk.End;
        }

        return length;
    }
}
