using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
var profile = builder.Configuration["Profile"] ?? "local";

// Ollama on the CPU with every GPU hidden (Chapter 3), in both profiles: in the hybrid one it answers the
// collections restricted to HR and Procurement, whose evidence never leaves Contoso's network (ADR-0001).
var ollama = builder.AddExecutable("ollama", builder.Configuration["Ollama:Path"] ?? "ollama", ".", "serve")
    .WithEnvironment("CUDA_VISIBLE_DEVICES", "-1")
    .WithEnvironment("GGML_VK_VISIBLE_DEVICES", "-1")
    .WithEnvironment("OLLAMA_HOST", "127.0.0.1:11434")
    .WithHttpEndpoint(port: 11434, isProxied: false)
    .WithHttpHealthCheck("/");

// The same API in both profiles; healthy means an index version is loaded (Chapter 24's alias).
var api = builder.AddProject<Projects.Ch25_Knowledge_Api>("api")
    .WithHttpHealthCheck("/health")
    .WaitFor(ollama);

if (profile == "local")
{
    api.WithEnvironment("AI__Provider", "Ollama")
        .WithEnvironment("AI__OllamaEndpoint", ollama.GetEndpoint("http"));
}
else
{
    // Hybrid: a cloud deployment writes the other answers; two replicas share answers through Redis.
    var endpoint = builder.AddParameter("model-endpoint");
    var deployment = builder.AddParameter("model-deployment");
    var cache = builder.AddRedis("cache");
    api.WithReplicas(2)
        .WithReference(cache).WaitFor(cache)
        .WithEnvironment("AI__Provider", "AzureOpenAI")
        .WithEnvironment("AI__Endpoint", endpoint)
        .WithEnvironment("AI__Model", deployment)
        .WithEnvironment("AI__Restricted__Provider", "Ollama")
        .WithEnvironment("AI__Restricted__OllamaEndpoint", ollama.GetEndpoint("http"))
        .WithEnvironment("AI__Restricted__OllamaModel", "llama3.2");
}

builder.Build().Run();
