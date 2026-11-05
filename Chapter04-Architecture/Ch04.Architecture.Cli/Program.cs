using Ch04.Architecture.Cli;
using Ch04.Knowledge.Core;
using Contoso.Corpus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Positional arguments are the command; --switches are configuration, such as --Pipeline:Retriever=Keyword.
var command = args.TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args[command.Length..],
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Services.AddKnowledgePipeline(builder.Configuration);
using var host = builder.Build();

var services = host.Services;
var (documents, chunks) = await services.GetRequiredService<IngestionPipeline>().RunAsync();
Console.WriteLine($"Indexed {documents} documents as {chunks} chunks.");
foreach (var (port, adapter) in Composition.Describe(services))
{
    Console.WriteLine($"  {port,-17} {adapter}");
}

Console.WriteLine();
switch (command.FirstOrDefault())
{
    case "ask":
        await Commands.AskAsync(services, command.ElementAtOrDefault(1) ?? "What torque applies to the M12 flange bolts on P-201?",
            builder.Configuration["as"] ?? People.Tomas.Id);
        break;
    case "budget":
        await Commands.BudgetAsync(services, builder.Configuration);
        break;
    default:
        Console.WriteLine("Usage: ask \"<question>\" [--as <user>] | budget   (add --Pipeline:Retriever=Keyword to swap the retriever)");
        break;
}
