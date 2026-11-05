using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Contoso.Knowledge.Security;

/// <summary>A model file against the SHA-256 its catalog entry recorded when it was chosen.</summary>
public sealed record ModelCheck(string Name, string Path, string Status);

public static class ModelProvenance
{
    /// <summary>Hashes every model file in the catalog that's present and compares it with the recorded hash.</summary>
    public static IReadOnlyList<ModelCheck> Verify(string catalogPath, string modelsRoot)
    {
        using var catalog = JsonDocument.Parse(File.ReadAllText(catalogPath));
        var checks = new List<ModelCheck>();
        foreach (var model in catalog.RootElement.GetProperty("models").EnumerateArray())
        {
            var (name, path) = (model.GetProperty("name").GetString()!, model.GetProperty("path").GetString()!);
            var file = System.IO.Path.Combine(modelsRoot, path);
            if (!model.TryGetProperty("sha256", out var expected))
            {
                checks.Add(new ModelCheck(name, path, "no hash recorded"));
                continue;
            }

            if (!File.Exists(file))
            {
                checks.Add(new ModelCheck(name, path, "not downloaded"));
                continue;
            }

            using var stream = File.OpenRead(file);
            var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
            checks.Add(new ModelCheck(name, path, actual == expected.GetString() ? "matches" : $"MISMATCH ({actual[..12]})"));
        }

        return checks;
    }
}

/// <summary>
/// The model catalog as a CycloneDX 1.6 ML-BOM: one component per file, weights as <c>machine-learning-model</c> and
/// vocabularies and configurations as <c>file</c>, each with its SHA-256, license and download address, so a scanner
/// or an auditor reads the models the way it reads the packages.
/// </summary>
public static class MlBom
{
    public static int Write(string catalogPath, string outputPath)
    {
        using var catalog = JsonDocument.Parse(File.ReadAllText(catalogPath));
        var components = catalog.RootElement.GetProperty("models").EnumerateArray().Select(m =>
        {
            var path = m.GetProperty("path").GetString()!;
            var license = m.GetProperty("license").GetString()!;
            return new Dictionary<string, object>
            {
                ["type"] = path.EndsWith(".onnx", StringComparison.Ordinal) || path.EndsWith(".traineddata", StringComparison.Ordinal) ? "machine-learning-model" : "file",
                ["bom-ref"] = m.GetProperty("name").GetString()!,
                ["name"] = m.GetProperty("name").GetString()!,
                ["description"] = m.GetProperty("notes").GetString()!,
                ["hashes"] = new[] { new { alg = "SHA-256", content = m.GetProperty("sha256").GetString() } },
                ["licenses"] = new[] { new { license = new { id = license.Split(' ')[0] } } },
                ["externalReferences"] = new[] { new { type = "distribution", url = m.GetProperty("url").GetString() } },
            };
        }).ToList();
        var bom = new Dictionary<string, object>
        {
            ["bomFormat"] = "CycloneDX",
            ["specVersion"] = "1.6",
            ["serialNumber"] = $"urn:uuid:{Guid.NewGuid()}",
            ["version"] = 1,
            ["metadata"] = new { timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), component = new { type = "application", name = "Contoso Knowledge Assistant" } },
            ["components"] = components,
        };
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(bom, new JsonSerializerOptions { WriteIndented = true }));
        return components.Count;
    }
}
