using System.Runtime.InteropServices;
using System.Text.Json;
using Ch03.Baseline.Cli;
using Ch03.Knowledge;
using Contoso.Corpus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

// The same composition as the API: models, middleware, vector store, index and assistant.
// Content root = the app's folder, so appsettings.json is found wherever the tool is started from.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Services.AddContosoKnowledge(builder.Configuration);

using var host = builder.Build();
await host.StartAsync(); // indexes the Markdown corpus

var options = host.Services.GetRequiredService<IOptions<KnowledgeOptions>>().Value;
var models = host.Services.GetRequiredService<ModelDescription>();
var indexing = host.Services.GetRequiredService<KnowledgeIndexer>().Report!;
var corpus = MarkdownLoader.LoadFolder(options.ContentPath);
var questions = SmokeSet.Load();

Console.WriteLine($"Chat model:  {models.Chat}");
Console.WriteLine($"Embeddings:  {models.Embeddings}");
Console.WriteLine($"Chunking:    fixed size, {options.ChunkTokens} tokens, {options.ChunkOverlapTokens} overlap; top {options.Top}");
Console.WriteLine($"Index:       {indexing.Documents} documents, {indexing.Chunks} chunks, {indexing.Tokens:N0} tokens, built in {indexing.Elapsed.TotalMilliseconds:F0} ms");
Console.WriteLine($"Machine:     {Environment.ProcessorCount} logical processors, {RuntimeInformation.FrameworkDescription}");
Console.WriteLine();

var runner = new SmokeRunner(
    host.Services.GetRequiredService<KnowledgeAssistant>(),
    host.Services.GetRequiredService<ChatMeasurements>(),
    corpus);
var results = await runner.RunAsync(questions, CancellationToken.None);

Report.PrintQuestions(results);
Report.PrintSummary(results, options.Top);

// --out results.json keeps every answer in full, for reading now and for Chapter 5's harness later.
if (builder.Configuration["out"] is { Length: > 0 } path)
{
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(results, new JsonSerializerOptions(JsonSerializerOptions.Web) { WriteIndented = true }));
    Console.WriteLine($"Wrote {path}");
}

await host.StopAsync();
