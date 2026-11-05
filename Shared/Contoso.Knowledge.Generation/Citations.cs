using System.Text;
using System.Text.RegularExpressions;

namespace Contoso.Knowledge.Generation;

/// <summary>
/// Checks an answer's claims after generation. A claim's source must be one the model was given and its quote must be
/// in that source. A quote that's found maps back to its span in the source document, which is what a source panel
/// highlights and an audit log keeps. The markers in the answer's text are written by code from the claims.
/// </summary>
public static partial class CitationValidator
{
    public static (IReadOnlyList<Claim> Claims, IReadOnlyList<string> Problems) Validate(ModelAnswer answer, IReadOnlyList<EvidenceBlock> evidence)
    {
        var claims = new List<Claim>();
        var problems = new List<string>();
        foreach (var claim in answer.Claims.Where(c => c.Text.Trim().Length > 0))
        {
            var id = claim.Source.Trim().Trim('[', ']');
            if (id.Length == 0)
            {
                // A sentence that cites nothing is allowed only to say what the sources don't contain.
                if (answer.Status is AnswerStatus.Answered) problems.Add($"a claim cites no source: \"{claim.Text}\"");
                claims.Add(new Claim(claim.Text.Trim(), "", "", false, false));
                continue;
            }

            if (evidence.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase)) is not { } block)
            {
                problems.Add($"{id} isn't one of the sources");
                claims.Add(new Claim(claim.Text.Trim(), id, claim.Quote, false, false));
                continue;
            }

            var span = Locate(claim.Quote, block);
            if (span is null) problems.Add($"the quote for {block.Id} isn't in it: \"{claim.Quote}\"");
            claims.Add(new Claim(claim.Text.Trim(), block.Id, claim.Quote, true, span is not null, block.DocumentId, span?.Start ?? -1, span?.End ?? -1));
        }

        return (claims, problems);
    }

    /// <summary>The answer's text: each claim followed by its source's marker, consecutive claims from one source marked once.</summary>
    public static string Render(IReadOnlyList<Claim> claims)
    {
        var text = new StringBuilder();
        for (var i = 0; i < claims.Count; i++)
        {
            if (i > 0) text.Append(' ');
            text.Append(claims[i].Text);
            var last = i == claims.Count - 1 || claims[i + 1].Source != claims[i].Source;
            if (claims[i].SourceExists && last) text.Append($" [{claims[i].Source}]");
        }

        return text.ToString();
    }

    /// <summary>
    /// Finds a quote in a block, ignoring case, spacing, typographic quotes and dashes, and Markdown table pipes. A quote
    /// with an ellipsis matches when its parts appear in order. Returns the span in the source document.
    /// </summary>
    public static (int Start, int End)? Locate(string quote, EvidenceBlock block)
    {
        var parts = quote.Split(["...", "…"], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Normalize(p, out _).Trim(' ', '"', '\'', '.', ',', ';', ':'))
            .Where(p => p.Length >= 3)
            .ToList();
        if (parts.Count == 0) return null;

        // Search the block's text as the model saw it, then map the match to segments and their document spans.
        var normalized = Normalize(block.Text, out var map);
        var (from, first, last) = (0, -1, -1);
        foreach (var part in parts)
        {
            var at = normalized.IndexOf(part, from, StringComparison.Ordinal);
            if (at < 0) return null;
            if (first < 0) first = at;
            (from, last) = (at + part.Length, at + part.Length - 1);
        }

        return (ToDocument(block, map[first]).Start, ToDocument(block, map[last]).End);
    }

    /// <summary>
    /// A character of the block's text as a range of its document: one character of a verbatim segment, or the whole
    /// row for a row rendered with its column names, whose characters aren't the document's.
    /// </summary>
    private static (int Start, int End) ToDocument(EvidenceBlock block, int index)
    {
        var at = 0;
        foreach (var segment in block.Segments)
        {
            if (index < at + segment.Text.Length + 1)
            {
                var offset = Math.Min(index - at, segment.Text.Length - 1);
                return segment.Verbatim ? (segment.Start + offset, segment.Start + offset + 1) : (segment.Start, segment.End);
            }

            at += segment.Text.Length + 1;   // segments are joined by a newline
        }

        return (block.Segments[^1].Start, block.Segments[^1].End);
    }

    /// <summary>Lowercases, folds typographic variants and collapses whitespace; <paramref name="map"/> points back to the original.</summary>
    public static string Normalize(string text, out int[] map)
    {
        var result = new StringBuilder(text.Length);
        var positions = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i] switch
            {
                '‘' or '’' or '`' => '\'',
                '“' or '”' => '"',
                '–' or '—' or '‑' => '-',
                '|' or '*' => ' ',
                var other => char.IsWhiteSpace(other) ? ' ' : char.ToLowerInvariant(other),
            };
            if (c == ' ' && (result.Length == 0 || result[^1] == ' ')) continue;
            result.Append(c);
            positions.Add(i);
        }

        map = [.. positions];
        return result.ToString();
    }

    /// <summary>The source ids in a text's markers, such as S1 and S2 in "... [S1] ... [S2]".</summary>
    public static IReadOnlyList<string> Markers(string text) =>
        [.. MarkerPattern().Matches(text).SelectMany(m => m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).Distinct()];

    [GeneratedRegex(@"\[(S?\d+(?:\s*,\s*S?\d+)*)\]")]
    private static partial Regex MarkerPattern();
}
