using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Contoso.Knowledge.Documents;

/// <summary>How canonical documents are stored as JSON: camel case, readable, nulls and empty defaults left out.</summary>
public static class DocumentJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(CanonicalDocument document) => JsonSerializer.Serialize(document, Options);

    public static CanonicalDocument Deserialize(string json) =>
        JsonSerializer.Deserialize<CanonicalDocument>(json, Options)
        ?? throw new InvalidDataException("The JSON doesn't hold a canonical document.");

    public static async Task<CanonicalDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<CanonicalDocument>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException($"{path} doesn't hold a canonical document.");
    }

    public static async Task SaveAsync(CanonicalDocument document, string path, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, document, Options, cancellationToken);
    }
}
