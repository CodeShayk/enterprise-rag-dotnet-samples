namespace Contoso.Knowledge.Evaluation;

/// <summary>One statement of an answer and the text of each source it cites.</summary>
public sealed record Statement(string Text, IReadOnlyList<string> Cited);

/// <summary>Citation recall and precision over a set of statements.</summary>
public sealed record CitationScore(int Statements, int Supported, int Citations, int Relevant)
{
    /// <summary>The share of statements that the sources they cite, read together, entail.</summary>
    public double Recall => Statements == 0 ? double.NaN : Supported / (double)Statements;

    /// <summary>The share of citations that support their statement or are needed for it.</summary>
    public double Precision => Citations == 0 ? double.NaN : Relevant / (double)Citations;
}

/// <summary>
/// Citation recall and precision as ALCE defines them (Gao et al., 2023), with an NLI model deciding support. A
/// statement counts as supported when the sources it cites, joined, entail it; one that cites nothing isn't. A citation
/// is relevant when it entails its statement alone, or when the statement's other citations no longer do without it.
/// </summary>
public sealed class CitationScorer(ClaimSupport support, double threshold = 0.5)
{
    public double Threshold => threshold;

    public CitationScore Score(IReadOnlyList<Statement> statements)
    {
        // Every premise each statement needs, scored in one batch: all its sources, each alone, all but each one.
        var items = new List<(string Evidence, string Claim)>();
        var joint = new int[statements.Count];
        var alone = new int[statements.Count][];
        var without = new int[statements.Count][];
        for (var s = 0; s < statements.Count; s++)
        {
            var (text, cited) = (statements[s].Text, statements[s].Cited);
            joint[s] = Add(cited.Count == 0 ? null : string.Join('\n', cited), text);
            alone[s] = cited.Count < 2 ? [joint[s]] : [.. cited.Select(c => Add(c, text))];
            without[s] = cited.Count < 2 ? [-1] : [.. cited.Select((_, i) => Add(string.Join('\n', cited.Where((_, j) => j != i)), text))];
        }

        var entailed = support.Score(items).Select(p => p >= threshold).ToArray();
        bool Holds(int item) => item >= 0 && entailed[item];
        var supported = statements.Where((_, s) => Holds(joint[s])).Count();
        var relevant = statements.Select((st, s) => st.Cited.Where((_, i) => Holds(alone[s][i]) || Holds(joint[s]) && !Holds(without[s][i])).Count()).Sum();
        return new CitationScore(statements.Count, supported, statements.Sum(st => st.Cited.Count), relevant);

        int Add(string? evidence, string claim)
        {
            if (evidence is null) return -1;
            items.Add((evidence, claim));
            return items.Count - 1;
        }
    }
}
