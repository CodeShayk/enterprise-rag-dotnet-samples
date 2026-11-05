using Ch14.Knowledge.Web;
using Ch14.Knowledge.Web.Components;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// A local model on a CPU can take a minute to finish an answer; the stream starts long before that.
builder.Services.AddHttpClient<KnowledgeClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Api:BaseAddress"] ?? "http://localhost:5140");
    client.Timeout = TimeSpan.FromMinutes(5);
});

var app = builder.Build();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
