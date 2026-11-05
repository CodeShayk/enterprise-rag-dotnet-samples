using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Contoso.AI;
using Contoso.Knowledge.Chunking;

namespace Contoso.Knowledge.Generation;

/// <summary>
/// The scripted model's rule for the grounding prompt, so the API and the tests run offline: it copies the source
/// sentence that shares the most words with the question and returns it as one claim, in the JSON the schema asks
/// for. It isn't a language model, and it says nothing it didn't copy.
/// </summary>
public static partial class ExtractiveScript
{
    public static ScriptRule Rule { get; } = new("grounded-claims", Reply: context =>
    {
        if (Question().Match(context.UserText) is not { Success: true } question) return null;
        var terms = ContosoScripts.Terms(WebUtility.HtmlDecode(question.Groups[1].Value));
        var best = Source().Matches(context.UserText)
            .SelectMany(m => Sentences.Split(m.Groups[2].Value).Select(s => (Id: m.Groups[1].Value, Text: m.Groups[2].Value[s.Start..s.End])))
            .Where(s => !s.Text.StartsWith('#'))
            .Select(s => (s.Id, s.Text, Score: ContosoScripts.Terms(s.Text).Count(terms.Contains)))
            .Where(s => s.Score > 0)
            .OrderByDescending(s => s.Score)
            .FirstOrDefault();

        ModelAnswer answer = best.Id is null
            ? new(AnswerStatus.NotFound, [new ModelClaim("The sources don't answer this question.", "", "")])
            : new(AnswerStatus.Answered, [new ModelClaim(best.Text, best.Id, best.Text)]);
        return ScriptReply.Say(JsonSerializer.Serialize(answer, JsonSerializerOptions.Web));
    });

    [GeneratedRegex(@"<question[^>]*>(.*?)</question>", RegexOptions.Singleline)]
    private static partial Regex Question();

    [GeneratedRegex(@"<source id=""(S\d+)""[^>]*>\n(.*?)\n</source>", RegexOptions.Singleline)]
    private static partial Regex Source();
}
