using Ch03.Knowledge.Web;
using Ch03.Knowledge.Web.Components;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// "http://api" is a service name: the AppHost supplies the address, and appsettings.json has
// the fallback (http://localhost:5030) for running without Aspire.
builder.Services.AddHttpClient<KnowledgeClient>(client => client.BaseAddress = new Uri("http://api"));

var app = builder.Build();
app.MapDefaultEndpoints();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
