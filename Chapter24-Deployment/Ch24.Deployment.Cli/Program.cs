using System.Text;
using Ch24.Deployment.Cli;

// Commands (from the sample-code folder):
//   candidates  candidate index configurations against the shipped one, on the dev and the test questions
//   release     release a candidate (child-200, parent-400, no-breadcrumbs, bge-fp32): build, gate on dev, swap the live
//               alias, check the test questions through it and roll back on a regression
//   degraded    the live index with the embedding model answering, down and slow
//   shadow      a stored version beside the live one on every golden question, compared by what each returned
Console.OutputEncoding = Encoding.UTF8;
var command = args.FirstOrDefault() ?? "candidates";
var lab = await Lab.LoadAsync();
switch (command)
{
    case "candidates":
        await CandidatesCommand.RunAsync(lab);
        break;
    case "release":
        await ReleaseCommand.RunAsync(lab, args.ElementAtOrDefault(1) ?? throw new ArgumentException("Name a candidate."));
        break;
    case "degraded":
        await DegradedCommand.RunAsync(lab);
        break;
    case "shadow":
        await ShadowCommand.RunAsync(lab, args.ElementAtOrDefault(1) ?? throw new ArgumentException("Name a stored version."));
        break;
    default:
        throw new ArgumentException($"Unknown command '{command}'.");
}
