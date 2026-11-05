using System.Text.Json;

namespace Contoso.Knowledge.Documents;

/// <summary>
/// The corpus that <c>Shared/Contoso.Corpus.Generator</c> writes to <c>sample-code/corpus</c>: source files by
/// system under <c>sources</c>, the register in which the source systems describe their files, and under
/// <c>truth</c> the canonical document each file was rendered from.
/// </summary>
public static class GeneratedCorpus
{
    private static readonly Lazy<string> RootFolder = new(FindRoot);

    public static string Root => RootFolder.Value;

    public static string Sources => Path.Combine(Root, "sources");

    public static string Truth => Path.Combine(Root, "truth");

    /// <summary>The register entries, one per file, as the source systems export them.</summary>
    public static IReadOnlyList<RegisterEntry> Register()
    {
        var path = Path.Combine(Sources, "manifest.json");
        return JsonSerializer.Deserialize<List<RegisterEntry>>(File.ReadAllText(path), DocumentJson.Options)
            ?? throw new InvalidDataException($"{path} isn't a register.");
    }

    public static Task<CanonicalDocument> TruthAsync(RegisterEntry entry, CancellationToken cancellationToken = default) =>
        DocumentJson.LoadAsync(Path.Combine(Root, entry.Truth), cancellationToken);

    public static string FullPath(RegisterEntry entry) => Path.Combine(Sources, entry.Path);

    private static string FindRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var corpus = Path.Combine(dir.FullName, "corpus");
                if (File.Exists(Path.Combine(corpus, "sources", "manifest.json"))) return corpus;
                if (File.Exists(Path.Combine(dir.FullName, "Contoso-Knowledge.slnx"))) break;
            }
        }

        throw new DirectoryNotFoundException(
            "The generated corpus is missing. From the sample-code folder, run: dotnet run --project Shared/Contoso.Corpus.Generator");
    }
}

/// <summary>
/// What a source system reports about one file. <see cref="Metadata"/> is kept as raw JSON because a system may
/// leave out fields the data contract requires; Chapter 6 validates it.
/// </summary>
public sealed record RegisterEntry(
    string Path, string System, string Id, string Version, string MediaType,
    DateTimeOffset LastModified, long Length, string Sha256, JsonElement Metadata, string Truth)
{
    public string? Note { get; init; }

    public string? CopyOf { get; init; }

    public IReadOnlyList<string> Attachments { get; init; } = [];
}
