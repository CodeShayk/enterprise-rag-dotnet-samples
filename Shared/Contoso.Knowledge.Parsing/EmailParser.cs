using System.Text.RegularExpressions;
using Contoso.Knowledge.Documents;
using MimeKit;
using Block = Contoso.Knowledge.Documents.Block;

namespace Contoso.Knowledge.Parsing;

/// <summary>
/// Email through MimeKit. The newest message's own text is content; the quoted history below it ("&gt;" lines and
/// "On ... wrote:" introductions) gets the quote role, because every earlier message of the thread is usually in
/// the corpus already. Attachments are returned for the pipeline to parse in turn.
/// </summary>
public sealed partial class EmailParser : IDocumentParser
{
    public string Name => "eml";

    public string Version => "1.0";

    public IReadOnlyList<string> MediaTypes { get; } = [Documents.MediaTypes.Eml];

    public async Task<ParsedContent> ParseAsync(Stream content, ParseContext context, CancellationToken cancellationToken = default)
    {
        var message = await MimeMessage.LoadAsync(content, cancellationToken);
        var body = message.TextBody ?? StripTags(message.HtmlBody ?? "");
        var blocks = Paragraphs(body);

        var attachments = new List<EmbeddedFile>();
        foreach (var attachment in message.Attachments.OfType<MimePart>().Where(a => a.Content is not null))
        {
            using var data = new MemoryStream();
            await attachment.Content!.DecodeToAsync(data, cancellationToken);
            attachments.Add(new EmbeddedFile(attachment.FileName ?? "attachment", attachment.ContentType.MimeType, data.ToArray()));
        }

        var properties = new Dictionary<string, string>
        {
            ["from"] = message.From.ToString(),
            ["to"] = message.To.ToString(),
            ["date"] = message.Date.ToString("O"),
        };
        if (message.Cc.Count > 0) properties["cc"] = message.Cc.ToString();
        if (message.MessageId is { } id) properties["messageId"] = id;
        if (message.InReplyTo is { } reply) properties["inReplyTo"] = reply;
        return new ParsedContent(blocks) { Title = message.Subject, Properties = properties, Attachments = attachments };
    }

    /// <summary>Splits the body into paragraphs; quoted lines lose their markers and become quoted paragraphs.</summary>
    public static List<Block> Paragraphs(string body)
    {
        var blocks = new List<Block>();
        var current = new List<string>();
        var currentQuoted = false;

        void Flush()
        {
            if (current.Count > 0)
            {
                var text = TextCleanup.Normalize(string.Join(' ', current));
                blocks.Add(currentQuoted ? new ParagraphBlock(text) { Role = BlockRoles.Quote } : new ParagraphBlock(text));
                current.Clear();
            }
        }

        foreach (var raw in body.ReplaceLineEndings("\n").Split('\n'))
        {
            var quoted = raw.StartsWith('>');
            var line = QuoteMarkers().Replace(raw, "").Trim();
            var intro = WroteLine().IsMatch(line);
            if (line.Length == 0 || quoted != currentQuoted || intro)
            {
                Flush();
            }

            currentQuoted = quoted || intro;
            if (line.Length == 0) continue;
            current.Add(line);
            if (intro) Flush();
        }

        Flush();
        return blocks;
    }

    private static string StripTags(string html) => TextCleanup.Normalize(Tags().Replace(html, " "));

    [GeneratedRegex(@"^(>\s?)+")]
    private static partial Regex QuoteMarkers();

    [GeneratedRegex(@"^On .+ wrote:$")]
    private static partial Regex WroteLine();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}

/// <summary>Outlook .msg files through MsgReader: subject, sender, sent date, plain-text body and attachments.</summary>
public sealed class MsgParser : IDocumentParser
{
    public string Name => "msg";

    public string Version => "1.0";

    public IReadOnlyList<string> MediaTypes { get; } = [Documents.MediaTypes.Msg];

    public Task<ParsedContent> ParseAsync(Stream content, ParseContext context, CancellationToken cancellationToken = default)
    {
        using var message = new MsgReader.Outlook.Storage.Message(content);
        var attachments = message.Attachments.OfType<MsgReader.Outlook.Storage.Attachment>()
            .Select(a => new EmbeddedFile(a.FileName, Documents.MediaTypes.FromFileName(a.FileName), a.Data))
            .ToList();
        var properties = new Dictionary<string, string>();
        if (message.Sender?.Email is { } from) properties["from"] = from;
        if (message.SentOn is { } sent) properties["date"] = sent.ToString("O");
        return Task.FromResult(new ParsedContent(EmailParser.Paragraphs(message.BodyText ?? ""))
        {
            Title = message.Subject,
            Properties = properties,
            Attachments = attachments,
        });
    }
}
