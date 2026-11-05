using Ch17.Knowledge.Mcp;
using Microsoft.AspNetCore.Authentication.JwtBearer;

// Contoso's document search as an MCP server: Streamable HTTP at http://localhost:5172/mcp. Every request needs a
// bearer token. In development, create one per person with
//   dotnet user-jwts create --project Chapter17-Agentic-RAG/Ch17.Knowledge.Mcp --name nadia.rahman
// which also stores the signing key in this project's user secrets; in production the token comes from Microsoft
// Entra ID (Chapter 22). The token says who is asking; what they may read comes from Contoso's directory.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddAuthorization();
builder.Services.AddSingleton(_ => DocumentSearch.LoadAsync().GetAwaiter().GetResult());
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<SearchTools>();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapMcp("/mcp").RequireAuthorization();
_ = app.Services.GetRequiredService<DocumentSearch>();   // build the index before the first request
app.Run();

/// <summary>Visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
