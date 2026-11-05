using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// Healthy means indexed: the API indexes the corpus before it starts listening.
var api = builder.AddProject<Projects.Ch03_Knowledge_Api>("api")
    .WithHttpHealthCheck("/health");

// The AI section of the AppHost's configuration (appsettings.json, user secrets or the command
// line) reaches the API as environment variables: AI:Provider becomes AI__Provider.
foreach (var setting in builder.Configuration.GetSection("AI").AsEnumerable().Where(s => s.Value is not null))
{
    api.WithEnvironment(setting.Key.Replace(":", "__"), setting.Value);
}

builder.AddProject<Projects.Ch03_Knowledge_Web>("web")
    .WithReference(api)
    .WaitFor(api);

builder.Build().Run();
