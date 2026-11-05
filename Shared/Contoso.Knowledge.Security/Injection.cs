using System.Text.RegularExpressions;

namespace Contoso.Knowledge.Security;

/// <summary>A piece of text that reads like an instruction to the model rather than content for a person.</summary>
public sealed record InjectionFinding(string Rule, string Match);

/// <summary>
/// A rule-based screen for instructions aimed at the model: text that addresses the assistant, tries to override its
/// rules, asks for its instructions or plants a link or image for the answer to carry out. It's a weak filter by
/// design (an attacker can reword), so it quarantines documents for review at ingestion and flags questions; the
/// prompt's delimiting and the checks on the answer still apply to whatever it misses.
/// </summary>
public static partial class InjectionDetector
{
    public static IReadOnlyList<InjectionFinding> Scan(string text) =>
    [
        .. Override().Matches(text).Select(m => new InjectionFinding("overrides the instructions", m.Value)),
        .. Address().Matches(text).Select(m => new InjectionFinding("addresses the assistant", m.Value)),
        .. Reveal().Matches(text).Select(m => new InjectionFinding("asks for the instructions", m.Value)),
        .. Role().Matches(text).Select(m => new InjectionFinding("a role marker", m.Value)),
        .. Image().Matches(text).Select(m => new InjectionFinding("an image or link with data in its address", m.Value)),
    ];

    [GeneratedRegex(@"\b(?:ignore|disregard|forget|override)\b[^.\n]{0,40}\b(?:instructions?|rules|sources|above|previous|prior)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Override();

    [GeneratedRegex(@"\b(?:note|message|instructions?) (?:to|for) (?:the )?(?:ai|assistant|model|chatbot|llm)\b|\b(?:ai|assistant|language model)s?,? (?:must|should|will) (?:now |always )?(?:say|tell|answer|reply|respond|include|add)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Address();

    [GeneratedRegex(@"\b(?:repeat|print|reveal|show|output)\b[^.\n]{0,30}\b(?:system prompt|instructions|your rules)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Reveal();

    [GeneratedRegex(@"(?m)^\s*(?:system|assistant|user)\s*(?:override)?\s*:|<\|(?:im_start|start_header_id|system)\|>|\bSYSTEM OVERRIDE\b", RegexOptions.IgnoreCase)]
    private static partial Regex Role();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\?[^)]*\)|\]\(https?://[^)]*[?&][^)]*=[^)]*\)")]
    private static partial Regex Image();
}
