using System.Text;
using Ch23.Governance.Cli;

// Commands (from the sample-code folder):
//   provenance  a provenance record for every answer over 60 simulated days (llama3.2, replayed from recordings; three
//               questions outside the golden set run live once and are recorded), the chain checked, who saw what
//   pii         personal data the corpus holds, found by patterns and the NER model, against Chapter 20's redactor
//   minimize    what masking personal data in the indexed text costs retrieval on the dev questions
//   erase       an erasure request through every store, proven; a tombstone, a legal hold and retention (run provenance first)
//   registry    the approved models and prompts, checked against every provenance record (run provenance first)
Console.OutputEncoding = Encoding.UTF8;
var command = args.FirstOrDefault() ?? "provenance";
var lab = await Lab.LoadAsync();
switch (command)
{
    case "provenance":
        await ProvenanceCommand.RunAsync(lab);
        break;
    case "pii":
        await PiiCommand.RunAsync(lab);
        break;
    case "minimize":
        await MinimizeCommand.RunAsync(lab);
        break;
    case "erase":
        await ErasureCommand.RunAsync(lab);
        break;
    case "registry":
        RegistryCommand.Run();
        break;
    default:
        throw new ArgumentException($"Unknown command '{command}'.");
}
