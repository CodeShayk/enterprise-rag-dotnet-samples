using System.Text.Json;

namespace Contoso.Knowledge.Operations;

/// <summary>
/// What built an index version: the chunker, whether breadcrumbs were indexed, the embedding model and the hash of its
/// file, the corpus it was built from, and when. The query side must use the same embedding model, so the manifest
/// travels with the version and a swap checks it.
/// </summary>
public sealed record IndexManifest(
    string Version, string Chunker, bool Breadcrumbs, string EmbeddingModel, string EmbeddingSha, int Chunks, string CorpusSha, DateTimeOffset BuiltAt);

/// <summary>Which version an alias points at, the one before it and when it changed.</summary>
public sealed record AliasState(string Version, string? Previous, DateTimeOffset SwappedAt);

/// <summary>
/// Index versions side by side under one folder, and aliases that name the version each service reads. A swap writes
/// the new alias to a temporary file and renames it over the old one, so a reader sees the old version or the new one,
/// never a mix; the previous version stays on disk until a later release removes it, so rollback is a second swap.
/// </summary>
public sealed class IndexCatalog(string root)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Root { get; } = Directory.CreateDirectory(root).FullName;

    public string PathOf(string version) => Directory.CreateDirectory(Path.Combine(Root, "versions", version)).FullName;

    public void SaveManifest(IndexManifest manifest) =>
        File.WriteAllText(Path.Combine(PathOf(manifest.Version), "manifest.json"), JsonSerializer.Serialize(manifest, Json));

    public IndexManifest Manifest(string version) =>
        JsonSerializer.Deserialize<IndexManifest>(File.ReadAllText(Path.Combine(PathOf(version), "manifest.json")), Json)!;

    public AliasState? Alias(string name)
    {
        var path = AliasPath(name);
        return File.Exists(path) ? JsonSerializer.Deserialize<AliasState>(File.ReadAllText(path), Json) : null;
    }

    /// <summary>Points the alias at a version whose embedding model matches the service's query encoder.</summary>
    public AliasState Swap(string name, string version, string queryEncoder, DateTimeOffset now)
    {
        var manifest = Manifest(version);
        if (manifest.EmbeddingModel != queryEncoder)
        {
            throw new InvalidOperationException($"Index {version} was embedded with {manifest.EmbeddingModel}; the service encodes queries with {queryEncoder}.");
        }

        var state = new AliasState(version, Alias(name)?.Version, now);
        var temporary = AliasPath(name) + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, Json));
        File.Move(temporary, AliasPath(name), overwrite: true);
        return state;
    }

    /// <summary>Points the alias back at the version it replaced.</summary>
    public AliasState Rollback(string name, string queryEncoder, DateTimeOffset now) =>
        Alias(name)?.Previous is { } previous ? Swap(name, previous, queryEncoder, now) : throw new InvalidOperationException($"Alias {name} has nothing to roll back to.");

    private string AliasPath(string name) => Path.Combine(Directory.CreateDirectory(Path.Combine(Root, "aliases")).FullName, name + ".json");
}
