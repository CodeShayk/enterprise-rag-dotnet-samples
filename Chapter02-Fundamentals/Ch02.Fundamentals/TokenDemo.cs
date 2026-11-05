using Contoso.Corpus;

namespace Ch02.Fundamentals;

/// <summary>The <c>tokens</c> command: how text becomes tokens, and what Chapter 1's prompt costs.</summary>
internal static class TokenDemo
{
    // Example rates in dollars per million tokens, not a price list: use your provider's current prices.
    private const decimal InputRate = 1.00m;
    private const decimal OutputRate = 4.00m;

    // The instructions, prompt and answer from Chapter 1 (Listing 1.1 and Output 1.1).
    private const string Instructions =
        "You are the Contoso Knowledge Assistant. Answer only from the numbered sources. " +
        "Cite each source you use as [n]. If the sources don't answer the question, say that you don't know.";
    private const string Question = "What torque applies to the M12 flange bolts on P-201?";
    private const string Answer =
        "Table 6-2 gives the tightening torque for the CP-200 flange bolts. Tighten the M12 flange bolts " +
        "(property class 8.8, lightly oiled) to 85 N·m. Apply the torque in a cross pattern in three passes: " +
        "30 N·m, 60 N·m, then 85 N·m. Re-check the torque after the first 24 hours of operation. [1]";

    public static void Run()
    {
        var o200k = new TokenCounter("o200k_base");
        var cl100k = new TokenCounter("cl100k_base");

        string[] samples =
        [
            Question,
            "Qual é o torque dos parafusos M12 da flange da bomba P-201?",
            "PN-4471-B",
            "seal-water pressure low",
        ];

        Console.WriteLine($"{"Text",-62}{"Chars",6}{"o200k",7}{"cl100k",8}");
        foreach (var text in samples)
        {
            Console.WriteLine($"{text,-62}{text.Length,6}{o200k.Count(text),7}{cl100k.Count(text),8}");
        }

        Console.WriteLine();
        foreach (var text in new[] { "PN-4471-B", "M12 flange bolts", "seal-water" })
        {
            Console.WriteLine($"o200k_base splits {text,-17} into {string.Join(" | ", o200k.Split(text))}");
        }

        var source = SeedDocuments.PumpManualTorque;
        var sourceBlock = $"[1] {source.Title} ({source.Id})\n{source.Text}\n\n";
        var questionLine = $"Question: {Question}";
        int instructions = o200k.Count(Instructions), block = o200k.Count(sourceBlock), question = o200k.Count(questionLine);
        int input = instructions + o200k.Count("Sources:\n") + block + question;
        int inputFive = instructions + o200k.Count("Sources:\n") + 5 * block + question;
        int output = o200k.Count(Answer);

        Console.WriteLine();
        Console.WriteLine($"Chapter 1's answer for Tomás (o200k_base): {input} input tokens " +
                          $"(instructions {instructions}, one source {block}, question {question}), {output} output tokens");
        Console.WriteLine($"With five sources of that size: {inputFive} input tokens");
        Console.WriteLine($"At example rates of ${InputRate:F2} and ${OutputRate:F2} per million input and output tokens:");
        foreach (var (label, inputTokens) in new[] { ("one source", input), ("five sources", inputFive) })
        {
            var perAnswer = (inputTokens * InputRate + output * OutputRate) / 1_000_000m;
            Console.WriteLine($"  {label,-13} ${perAnswer:F6} per answer, ${perAnswer * 10_000 * 30:F2} for 10,000 answers a day for 30 days");
        }
    }
}
