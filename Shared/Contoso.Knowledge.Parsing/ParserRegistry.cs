using Contoso.Knowledge.Documents;

namespace Contoso.Knowledge.Parsing;

/// <summary>
/// Picks the parser for a file's media type and runs it inside the limits every parser of untrusted
/// input needs: a size cap, a timeout and, for zip-based formats, an expansion and macro check first.
/// </summary>
public sealed class ParserRegistry(IEnumerable<IDocumentParser> parsers)
{
    private readonly Dictionary<string, IDocumentParser> _byMediaType =
        parsers.SelectMany(p => p.MediaTypes.Select(m => (m, p))).ToDictionary(x => x.m, x => x.p, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every parser in this library with its default options.</summary>
    public static ParserRegistry Default { get; } = new(
    [
        new PdfParser(), new HtmlParser(), new MarkdownParser(), new DocxParser(), new PptxParser(),
        new XlsxParser(), new CsvParser(), new EmailParser(), new MsgParser(),
    ]);

    public IReadOnlyCollection<IDocumentParser> Parsers => _byMediaType.Values.Distinct().ToList();

    public IDocumentParser? For(string mediaType) => _byMediaType.GetValueOrDefault(mediaType);

    public async Task<ParsedContent> ParseAsync(Stream content, ParseContext context, CancellationToken cancellationToken = default)
    {
        var parser = For(context.MediaType)
            ?? throw new UnsupportedFormatException($"No parser for {context.MediaType} ({context.FileName}).");

        // Parsers get a seekable copy: several need random access, and the size cap is checked as we copy.
        var buffer = new MemoryStream();
        await CopyWithLimitAsync(content, buffer, context.Limits.MaxInputBytes, cancellationToken);
        buffer.Position = 0;
        if (PackageGuard.IsZipPackage(context.MediaType))
        {
            PackageGuard.Check(buffer, context.Limits);
            buffer.Position = 0;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(context.Limits.Timeout);
        try
        {
            // Run on the thread pool so that a parser stuck in a tight loop still lets the timeout fire.
            return await Task.Run(() => parser.ParseAsync(buffer, context, timeout.Token), timeout.Token).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ParserLimitException($"{context.FileName}: parsing took longer than {context.Limits.Timeout.TotalSeconds:N0} s.");
        }
        catch (Exception e) when (e is not (OperationCanceledException or ParserLimitException or UnsupportedFormatException))
        {
            // Every library throws its own types for a broken file; callers only need to know that this file can't be read.
            throw new DocumentParseException($"{context.FileName}: {e.GetType().Name}: {e.Message}", e);
        }
    }

    private static async Task CopyWithLimitAsync(Stream source, Stream target, long limit, CancellationToken cancellationToken)
    {
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new ParserLimitException($"The file is larger than the {limit / 1024 / 1024} MB limit.");
            }

            await target.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }
}

/// <summary>The file is valid but no parser reads its format.</summary>
public sealed class UnsupportedFormatException(string message) : Exception(message);

/// <summary>The file is broken or malformed for its format. Parsing the same bytes again fails the same way.</summary>
public sealed class DocumentParseException(string message, Exception inner) : Exception(message, inner);

/// <summary>The file broke a resource limit: too large, expands too far, takes too long or carries macros.</summary>
public sealed class ParserLimitException(string message) : Exception(message);
