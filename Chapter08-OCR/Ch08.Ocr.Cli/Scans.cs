using Contoso.Knowledge.Documents;

namespace Ch08.Ocr.Cli;

/// <summary>The corpus's scanned documents and the text printed on their scanned pages, from the truth they were rendered from.</summary>
public static class Scans
{
    public sealed record ScannedDocument(RegisterEntry Entry, CanonicalDocument Truth, IReadOnlyList<int> Pages, byte[] Pdf)
    {
        public string Name => Path.GetFileNameWithoutExtension(Entry.Path);
    }

    /// <summary>Every document with pages that have no text layer, copies left out.</summary>
    public static async Task<List<ScannedDocument>> AllAsync(string? filter = null)
    {
        var documents = new List<ScannedDocument>();
        foreach (var entry in GeneratedCorpus.Register().Where(e => e.CopyOf is null && e.MediaType == MediaTypes.Pdf))
        {
            if (filter is not null && !entry.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var truth = await GeneratedCorpus.TruthAsync(entry);
            var scanned = truth.Pages.Where(p => !p.HasTextLayer).Select(p => p.Number).ToList();
            if (scanned.Count == 0) continue;
            documents.Add(new ScannedDocument(entry, truth, scanned, await File.ReadAllBytesAsync(GeneratedCorpus.FullPath(entry))));
        }

        return documents;
    }

    /// <summary>
    /// The body text printed on the scanned pages, in reading order: headings, paragraphs, list items, table
    /// captions and rows, figure labels and captions. Running headers and footers are left out, and so are marks
    /// added on paper (stamps, signatures, handwriting), which the truth doesn't have.
    /// </summary>
    public static string BodyText(ScannedDocument document) =>
        string.Join('\n', document.Truth.Blocks.Where(b => b.Role is null && b.Anchor?.Page is { } p && document.Pages.Contains(p)).Select(b => b switch
        {
            HeadingBlock h => h.Text,
            ParagraphBlock p => p.Text,
            ListItemBlock l => l.Text,
            TableBlock t => string.Join('\n', (t.Caption is null ? [] : new[] { t.Caption }).Concat(t.Rows.Select(r => string.Join(' ', r)))),
            FigureBlock f => string.Join('\n', f.Labels.Append(f.Caption ?? "")),
            _ => "",
        }));
}
