using System.Text;
using Ch18.Multimodal.Cli;

// Commands (from the sample-code folder):
//   inventory               where Contoso's evidence lives: PDF pages and scans, tables and figures, golden questions by modality
//   pages                   page-level search by text (BM25), by image (CLIP) and fused, with each representation's CPU cost
//   table-chunks            table retrieval: Markdown runs, the header row in every run, and a summary per table
//   tables [--split test]   numeric fidelity and quote location for answers over tables (Chapter 14's recorded answers)
//   route                   modality routing by rules and by nearest example; a table filter against none
//   lab [--id G001]         Tomás's torque question with its citation highlighted on the scanned page
//   vision                  Granite Vision 3.2 2B on the CPU: figure captions and the torque table read from the scan
// Page units are built once and cached in runs/ch18/pages.json; answers replay from Chapter 14's recordings, and the
// vision model's replies are cached in runs/ch18/vlm-cache.jsonl. New model calls need Ollama with llama3.2 and
// granite3.2-vision:2b; CLIP needs `python eng/get-models.py`.
Console.OutputEncoding = Encoding.UTF8;
var command = args.FirstOrDefault() ?? "inventory";
string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
var lab = await Lab.LoadAsync();

await (command switch
{
    "inventory" => Task.Run(() => Commands.Inventory(lab)),
    "pages" => Commands.PagesAsync(lab),
    "table-chunks" => Commands.TableChunksAsync(lab),
    "tables" => Commands.TablesAsync(lab, Option("--split") ?? "dev"),
    "route" => Commands.RouteAsync(lab),
    "lab" => Commands.LabAsync(lab, Option("--id") ?? "G001"),
    "vision" => Commands.VisionModelAsync(lab),
    _ => throw new ArgumentException($"Unknown command '{command}'."),
});
