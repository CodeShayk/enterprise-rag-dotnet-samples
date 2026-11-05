using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Contoso.Corpus;

namespace Contoso.Knowledge.Observability;

/// <summary>How much of a question or an answer telemetry may carry.</summary>
public enum ContentCapture
{
    /// <summary>Lengths, IDs, scores and versions only: the default, as in the GenAI conventions.</summary>
    Off,

    /// <summary>The text with e-mail addresses, phone numbers and people's names replaced.</summary>
    Redacted,

    /// <summary>The text as asked: for a debugging session, behind its own switch and a short retention.</summary>
    Full,
}

/// <summary>
/// Content for telemetry under a capture mode, and pseudonyms for people. A pseudonym is a keyed hash: stable enough
/// to count one user's requests and to find their traces for an access request, useless to anyone without the key.
/// </summary>
public static partial class TelemetryContent
{
    public static string? Text(string text, ContentCapture mode) => mode switch
    {
        ContentCapture.Off => null,
        ContentCapture.Redacted => Redact(text),
        _ => text,
    };

    public static string Redact(string text)
    {
        var redacted = Email().Replace(text, "[email]");
        redacted = Phone().Replace(redacted, "[phone]");
        foreach (var name in People.All.SelectMany(p => new[] { p.Name, p.Name.Split(' ')[0] }).Distinct().OrderByDescending(n => n.Length))
        {
            redacted = Regex.Replace(redacted, $@"\b{Regex.Escape(name)}\b", "[person]");
        }

        return redacted;
    }

    public static string Pseudonym(string userId, byte[] key) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(userId)))[..16];

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(\.[\w-]+)+")]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<![\w-])(?:\+\d{1,3}[ .-]?)?(?:\(\d{2,4}\)[ .-]?|\d{3}[ .-])\d{3}[ .-]\d{4}(?![\w-])")]
    private static partial Regex Phone();
}
