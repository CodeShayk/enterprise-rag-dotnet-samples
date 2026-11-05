using Contoso.Knowledge.Generation;

namespace Contoso.Knowledge.Agents;

/// <summary>
/// The evidence one request gathered through its tools, numbered as the answer will cite it. Passages and query
/// results become the same kind of source, so Chapter 14's generator cites a row of the work orders the way it cites a
/// manual, and its quote validation checks both.
/// </summary>
public sealed class EvidenceLedger
{
    public const string RecordsDocument = "RECORDS";

    private readonly List<EvidenceBlock> _blocks = [];

    public IReadOnlyList<EvidenceBlock> Blocks => _blocks;

    /// <summary>Adds passages after the sources already here; a passage found again keeps its first number.</summary>
    public IReadOnlyList<EvidenceBlock> AddPassages(IEnumerable<EvidenceBlock> blocks) =>
        [.. blocks.Select(block => _blocks.FirstOrDefault(b => b.DocumentId != RecordsDocument && b.Reference == block.Reference) ?? Add(block))];

    /// <summary>A query's rows as a source: titled with what was asked, the query itself as its section.</summary>
    public EvidenceBlock AddQuery(QueryResult result, string title, string? summary = null)
    {
        var text = summary is null ? result.Render() : result.Rows.Count == 0 ? summary : $"{summary}\n{result.Render()}";
        return Add(new EvidenceBlock("", RecordsDocument, title, result.Sql, "query-result", RecordsDatabase.Today,
            [new EvidenceSegment(text, 0, text.Length, Verbatim: false)], 1.0));
    }

    private EvidenceBlock Add(EvidenceBlock block)
    {
        var numbered = block with { Id = $"S{_blocks.Count + 1}" };
        _blocks.Add(numbered);
        return numbered;
    }

    /// <summary>What a tool shows the model it called: each source's id, title and text.</summary>
    public static string Describe(IEnumerable<EvidenceBlock> blocks) =>
        string.Join("\n\n", blocks.Select(b => $"[{b.Id}] {b.Title}{(b.DocumentId != RecordsDocument && b.Section is { } s ? $", {s}" : "")}\n{b.Text}"));
}
