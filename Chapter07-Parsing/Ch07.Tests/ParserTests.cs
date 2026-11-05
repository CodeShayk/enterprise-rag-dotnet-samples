using System.IO.Compression;
using System.Text;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Parsing;

namespace Ch07.Tests;

/// <summary>
/// Golden outputs: each format's parse of a generated corpus file, block by block with kind and role, compared with
/// an approved snapshot. A parser change that moves one block, drops a role or rewords a cell shows up here first.
/// </summary>
public sealed class GoldenOutputTests
{
    [Theory]
    [InlineData("fileshare/fluid/sops/SOP-FS-014 Mechanical seal replacement Rev 3.docx", "sop-fs-014-rev3")]
    [InlineData("fileshare/fluid/training/CP-200 seal replacement training.pptx", "seal-training-deck")]
    [InlineData("intranet/safety/sb-0932.html", "sb-0932")]
    [InlineData("fileshare/procurement/Parts Catalog 2027.xlsx", "parts-catalog")]
    [InlineData("mail/service/2026-11-07 Pumping station 3 E-217 trips.eml", "e217-thread")]
    [InlineData("fileshare/motion/manuals/HG-400 Gearbox Manual 2021.pdf", "hg400-manual")]
    [InlineData("fileshare/motion/manuals/RX Coupling Installation.pdf", "rx-coupling")]
    public async Task Each_format_parses_to_its_approved_blocks(string path, string snapshot)
    {
        Assert.SkipUnless(CorpusExists(), "Generate the corpus first: dotnet run --project Shared/Contoso.Corpus.Generator");
        var file = Path.Combine(GeneratedCorpus.Sources, path);
        await using var stream = File.OpenRead(file);
        var parsed = await ParserRegistry.Default.ParseAsync(stream, new ParseContext(Path.GetFileName(file), MediaTypes.FromFileName(file)), TestContext.Current.CancellationToken);
        var lines = parsed.Blocks.Select(b => $"{b.GetType().Name.Replace("Block", "").ToLowerInvariant()}{(b.Role is null ? "" : $" ({b.Role})")}: {b.ToMarkdown().ReplaceLineEndings(" / ")}");
        Snapshot.Match(string.Join('\n', lines) + "\n", snapshot);
    }

    internal static bool CorpusExists()
    {
        try
        {
            return Directory.Exists(GeneratedCorpus.Sources);
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }
}

/// <summary>Files are untrusted input: each of these must fail fast and safely, never hang or leak.</summary>
public sealed class HardeningTests
{
    private static Task<ParsedContent> Parse(byte[] bytes, string name, ParserLimits? limits = null, ParserRegistry? registry = null) =>
        (registry ?? ParserRegistry.Default).ParseAsync(new MemoryStream(bytes), new ParseContext(name, MediaTypes.FromFileName(name)) { Limits = limits ?? ParserLimits.Default },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_zip_bomb_is_rejected_before_any_parser_opens_it()
    {
        var bomb = Package(("[Content_Types].xml", "<Types/>"u8.ToArray()), ("word/document.xml", new byte[20 * 1024 * 1024]));
        var error = await Assert.ThrowsAsync<ParserLimitException>(() => Parse(bomb, "bomb.docx"));
        Assert.Contains("zip bomb", error.Message);
    }

    [Fact]
    public async Task A_document_with_macros_is_refused()
    {
        var macro = Package(("[Content_Types].xml", "<Types/>"u8.ToArray()), ("word/vbaProject.bin", [1, 2, 3]));
        var error = await Assert.ThrowsAsync<ParserLimitException>(() => Parse(macro, "invoice.docx"));
        Assert.Contains("macros", error.Message);
    }

    [Fact]
    public async Task An_external_entity_is_never_resolved()
    {
        // A Word file whose body declares an entity that points at a local file (XXE).
        const string body = """
            <?xml version="1.0" encoding="UTF-8"?><!DOCTYPE w:document [<!ENTITY xxe SYSTEM "file:///C:/Windows/win.ini">]>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>&xxe;</w:t></w:r></w:p></w:body></w:document>
            """;
        var docx = Package(
            ("[Content_Types].xml", Encoding.UTF8.GetBytes("""<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>""")),
            ("_rels/.rels", Encoding.UTF8.GetBytes("""<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="r1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""")),
            ("word/document.xml", Encoding.UTF8.GetBytes(body.Trim())));
        var error = await Assert.ThrowsAsync<DocumentParseException>(() => Parse(docx, "xxe.docx"));
        Assert.DoesNotContain("[fonts]", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_file_over_the_size_limit_is_rejected_while_it_is_read()
    {
        await Assert.ThrowsAsync<ParserLimitException>(() => Parse(new byte[4096], "big.csv", new ParserLimits { MaxInputBytes = 1024 }));
    }

    [Fact]
    public async Task A_parser_that_hangs_is_abandoned_at_the_timeout()
    {
        var registry = new ParserRegistry([new HangingParser()]);
        var started = DateTime.UtcNow;
        await Assert.ThrowsAsync<ParserLimitException>(() => Parse([1], "slow.csv", new ParserLimits { Timeout = TimeSpan.FromMilliseconds(200) }, registry));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_truncated_pdf_fails_as_a_parse_error()
    {
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << /Type /Catalog >> endobj\n");
        await Assert.ThrowsAsync<DocumentParseException>(() => Parse(pdf, "broken.pdf"));
    }

    private static byte[] Package(params (string Name, byte[] Data)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                entry.Write(data);
            }
        }

        return stream.ToArray();
    }

    private sealed class HangingParser : IDocumentParser
    {
        public string Name => "hang";

        public string Version => "1";

        public IReadOnlyList<string> MediaTypes { get; } = [Contoso.Knowledge.Documents.MediaTypes.Csv];

        public Task<ParsedContent> ParseAsync(Stream content, ParseContext context, CancellationToken cancellationToken = default)
        {
            Thread.Sleep(3000);   // a parser stuck in native code that ignores cancellation
            return Task.FromResult(new ParsedContent([]));
        }
    }
}

public sealed class FormatDetailTests
{
    [Fact]
    public void Csv_fields_keep_their_commas_quotes_and_line_breaks()
    {
        var rows = CsvParser.Parse("id,description\r\nWO-1,\"chg seal, LOTO ok\"\r\nWO-2,\"said \"\"done\"\"\nnext line\"\r\n");
        Assert.Equal(["WO-1", "chg seal, LOTO ok"], rows[1]);
        Assert.Equal(["WO-2", "said \"done\"\nnext line"], rows[2]);
    }

    [Fact]
    public void Quoted_history_in_an_email_gets_the_quote_role()
    {
        var blocks = EmailParser.Paragraphs("Report attached.\n\nKwame\n\nOn Thu, 5 Nov 2026 at 09:40, Pat Lee wrote:\n> Can someone come out?\n>\n> Pat\n");
        Assert.Equal(["Report attached.", "Kwame"], blocks.Where(b => b.Role is null).Select(b => b.ToMarkdown()));
        Assert.Equal(3, blocks.Count(b => b.Role == BlockRoles.Quote));
    }

    [Fact]
    public async Task Tracked_changes_are_accepted_and_list_numbers_rebuilt()
    {
        Assert.SkipUnless(GoldenOutputTests.CorpusExists(), "Generate the corpus first.");
        var file = Path.Combine(GeneratedCorpus.Sources, "fileshare/fluid/sops/SOP-FS-014 Mechanical seal replacement Rev 3.docx");
        var bytes = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
        var proper = await new DocxParser().ParseAsync(new MemoryStream(bytes), new ParseContext("sop.docx", MediaTypes.Docx), TestContext.Current.CancellationToken);
        var naive = await new DocxParser(new DocxParserOptions { Naive = true }).ParseAsync(new MemoryStream(bytes), new ParseContext("sop.docx", MediaTypes.Docx), TestContext.Current.CancellationToken);

        Assert.Contains(proper.Blocks, b => b.ToMarkdown() == "4. Fit mechanical seal PN-4471-C. Don't mix PN-4471-B and PN-4471-C parts.");
        Assert.Contains(naive.Blocks, b => b.ToMarkdown().Contains("PN-4471-BPN-4471-C", StringComparison.Ordinal));
    }
}
