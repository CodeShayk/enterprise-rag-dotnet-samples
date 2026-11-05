using System.Text.RegularExpressions;

namespace Contoso.Knowledge.Governance;

/// <summary>Personal data found in a text: an email address, a phone number or a person's name.</summary>
public sealed record PiiFinding(string Kind, string Text);

/// <summary>
/// Finds personal data at ingestion: patterns for email addresses and phone numbers, and a name finder (a NER model,
/// passed in) for people the directory doesn't know, such as a customer's contact. <see cref="Minimize"/> replaces what
/// it found before the text reaches an index, for collections whose answers don't need it.
/// </summary>
public sealed partial class PiiScanner(Func<string, IEnumerable<string>> people)
{
    public IReadOnlyList<PiiFinding> Scan(string text) =>
    [
        .. Email().Matches(text).Select(m => new PiiFinding("email", m.Value)),
        .. Phone().Matches(text).Select(m => new PiiFinding("phone", m.Value)),
        .. people(text).Distinct().Select(n => new PiiFinding("person", n)),
    ];

    public string Minimize(string text, IReadOnlyList<PiiFinding> findings)
    {
        var minimized = text;
        foreach (var f in findings.OrderByDescending(f => f.Text.Length)) minimized = minimized.Replace(f.Text, $"[{f.Kind}]", StringComparison.Ordinal);
        return minimized;
    }

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(\.[\w-]+)+")]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<![\w-])(?:\+\d{1,3}[ .-]?)?(?:\(\d{2,4}\)[ .-]?|\d{3}[ .-])\d{3}[ .-]\d{4}(?![\w-])")]
    private static partial Regex Phone();
}
