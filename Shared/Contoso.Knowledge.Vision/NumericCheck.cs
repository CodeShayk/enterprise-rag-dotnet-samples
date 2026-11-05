using System.Globalization;
using System.Text.RegularExpressions;

namespace Contoso.Knowledge.Vision;

/// <summary>A number in text with its unit, the unit normalized: N·m, Nm and N-m are all "N·m"; h, hours and operating hours are "h".</summary>
public sealed record Quantity(decimal Value, string Unit, string Text);

/// <summary>
/// Numeric fidelity for answers over tables: every quantity a claim states must be one its cited source states, with
/// the same unit. A wrong number fails, and so does the right number in the wrong unit; "85 Nm" against "85 N·m" passes.
/// </summary>
public static partial class NumericCheck
{
    public static IReadOnlyList<Quantity> Quantities(string text) =>
        [.. Pattern().Matches(text).Select(m => new Quantity(
            decimal.Parse(m.Groups["n"].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture),
            Unit(m.Groups["u"].Value), m.Value.Trim()))];

    /// <summary>
    /// The quantities in a claim that its source doesn't state, and why: no such number, or the number in another unit.
    /// Numbers the question gave (such as "size 50") count as known.
    /// </summary>
    public static IReadOnlyList<(Quantity Claimed, string Problem)> Check(string claim, string source, string question = "")
    {
        var stated = Quantities(source);
        var asked = Quantities(question).Select(q => q.Value).ToHashSet();
        var problems = new List<(Quantity, string)>();
        foreach (var q in Quantities(claim).Where(q => !asked.Contains(q.Value)))
        {
            var same = stated.Where(s => s.Value == q.Value).ToList();
            if (same.Count == 0) problems.Add((q, "not in the source"));
            else if (q.Unit.Length > 0 && same.All(s => s.Unit.Length > 0 && s.Unit != q.Unit)) problems.Add((q, $"the source says {same[0].Text}"));
        }

        return problems;
    }

    public static string Unit(string unit) => unit.Trim().ToLowerInvariant() switch
    {
        "" => "",
        "n·m" or "nm" or "n-m" or "n m" or "n.m" => "N·m",
        "h" or "hr" or "hrs" or "hour" or "hours" or "operating hours" => "h",
        "month" or "months" => "month",
        "week" or "weeks" => "week",
        "year" or "years" => "year",
        "day" or "days" => "day",
        "°c" or "c" or "degrees" => "°C",
        "bar" => "bar",
        "mm" => "mm",
        "µm" or "um" or "micron" or "microns" => "µm",
        "kw" => "kW",
        "rpm" => "rpm",
        "%" or "percent" => "%",
        "l" or "litre" or "litres" or "liter" or "liters" => "l",
        "g" => "g",
        var other => other,
    };

    [GeneratedRegex(@"(?<![\w.-])(?<n>\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?)\s?(?<u>N·m|N-m|N\.m|Nm|N m|operating hours|hours?|hrs?|h|months?|weeks?|years?|days?|°C|bar|mm|µm|um|microns?|kW|rpm|%|percent|litres?|liters?|l|g)?(?![\w·])", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();
}
