using Ch05.Evaluation.Cli;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

// Positional arguments are the command; --switches are configuration, such as --Evaluation:Split=test.
var command = args.TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args[command.Length..],
    ContentRootPath = AppContext.BaseDirectory,
});
var context = EvaluationContext.Load(builder.Configuration);

switch (command.FirstOrDefault())
{
    case "validate":
        return Commands.Validate(context);
    case "run":
        await Commands.RunAsync(context);
        return 0;
    case "compare":
        await Commands.CompareAsync(context, command.ElementAtOrDefault(1) ?? "vector-100", command.ElementAtOrDefault(2) ?? "keyword-100");
        return 0;
    case "power":
        Commands.PrintPower(builder.Configuration.GetValue("discordant", 0.25));
        return 0;
    case "generate":
        await ModelCommands.GenerateAsync(context, builder.Configuration);
        return 0;
    case "annotate":
        await ModelCommands.AnnotateAsync(context, builder.Configuration);
        return 0;
    default:
        Console.WriteLine("Usage: validate | run | compare <baseline> <candidate> | power [--discordant 0.25] | generate | annotate");
        Console.WriteLine("       options: --Evaluation:Split=dev|test|all  --Evaluation:K=4");
        return 1;
}
