using System.Text;
using Ch01.WhyRag;
using Contoso.AI;
using Contoso.Corpus;
using Microsoft.Extensions.Configuration;

// appsettings.json, then AI__* environment variables, then arguments such as --AI:Provider=Ollama.
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

Console.OutputEncoding = Encoding.UTF8;
var options = AIClientFactory.ReadOptions(configuration);
using var chat = AIClientFactory.CreateChatClient(options);
var rag = new TinyRag(chat, new KeywordRetriever(SeedDocuments.All));

Console.WriteLine($"Model: {options.Describe()}");
Console.WriteLine($"Corpus: {SeedDocuments.All.Count} Contoso documents\n");

const string Torque = "What torque applies to the M12 flange bolts on P-201?";
const string Leave = "What changed in the parental leave policy this year?";
const string Salary = "What is the CEO's salary?";

Print("Model only", People.Tomas, Torque, await rag.AskModelOnlyAsync(Torque));
await Ask(People.Tomas, Torque);
await Ask(People.Daniel, Leave);
await Ask(People.Tomas, Leave);
await Ask(People.Tomas, Salary);

async Task Ask(Person asker, string question)
{
    var answer = await rag.AnswerAsync(question, asker);
    var found = answer.Sources.Count == 0
        ? "nothing this person may read"
        : string.Join(", ", answer.Sources.Select(s => $"{s.Document.Id} ({s.Score} words in common)"));
    Print("RAG", asker, question, $"retrieved: {found}\n{answer.Text}");
}

static void Print(string mode, Person asker, string question, string text)
{
    Console.WriteLine($"[{mode}] {asker.Name}: {question}");
    foreach (var line in text.Split('\n'))
    {
        foreach (var wrapped in Wrap(line, 86))
        {
            Console.WriteLine($"  {wrapped}");
        }
    }

    Console.WriteLine();
}

static IEnumerable<string> Wrap(string text, int width)
{
    var line = new StringBuilder();
    foreach (var word in text.Split(' '))
    {
        if (line.Length > 0 && line.Length + word.Length + 1 > width)
        {
            yield return line.ToString();
            line.Clear();
        }

        line.Append(line.Length > 0 ? " " : string.Empty).Append(word);
    }

    yield return line.ToString();
}
