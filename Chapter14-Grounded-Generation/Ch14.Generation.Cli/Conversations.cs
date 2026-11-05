using System.Text.RegularExpressions;
using Contoso.Knowledge.Evaluation;

namespace Ch14.Generation.Cli;

/// <summary>Chapter 13's follow-ups: a two-part question asked as its first part and then "And ..." for the second.</summary>
public static partial class Conversations
{
    public sealed record Conversation(GoldenQuestion Original, string First, string FollowUp);

    public static IReadOnlyList<Conversation> From(IEnumerable<GoldenQuestion> questions) =>
        [.. questions.Select(q => (q, m: TwoPart().Match(q.Question))).Where(x => x.m.Success)
            .Select(x => new Conversation(x.q, x.m.Groups[1].Value + "?", "And " + char.ToLowerInvariant(x.m.Groups[2].Value[0]) + x.m.Groups[2].Value[1..] + "?"))
            .GroupBy(c => c.FollowUp).Select(g => g.First())];

    [GeneratedRegex(@"^((?:What|Which|How|Who|When|Why|Is|Does|Can)\b.+?), and (.+)\?$")]
    private static partial Regex TwoPart();
}
