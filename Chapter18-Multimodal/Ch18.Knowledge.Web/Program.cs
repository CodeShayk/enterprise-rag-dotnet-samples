using Ch18.Knowledge.Web;
using Ch18.Knowledge.Web.Components;
using Contoso.Knowledge.Documents;
using Contoso.Knowledge.Vision;

// The viewer takes the reader from ?as=tomas.silva, a development stand-in: in production the reader is the
// signed-in user (Chapter 22), never a parameter a link can carry.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents();
builder.Services.AddSingleton<DocumentLibrary>();

var app = builder.Build();
app.UseAntiforgery();
app.MapStaticAssets();

// The page as a PNG, with the cited region highlighted when the link carries one: what an answer embeds or prints.
app.MapGet("/documents/{id}/pages/{page:int}.png", async (string id, int page, string? box, string? @as, DocumentLibrary library) =>
{
    if (await library.OpenAsync(id, page, @as) is not { } pdf) return Results.NotFound();
    BoundingBox[] boxes = VisualCitation.Parse(id, page, box) is { } citation ? [citation.Box] : [];
    return Results.File(PageRenders.Highlight(pdf.Bytes, page, boxes), "image/png");
});

app.MapRazorComponents<App>();
app.Run();

public partial class Program;
