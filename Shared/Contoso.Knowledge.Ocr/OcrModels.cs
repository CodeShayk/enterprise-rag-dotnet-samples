namespace Contoso.Knowledge.Ocr;

/// <summary>
/// Finds the models <c>eng/get-models.py</c> downloads into <c>sample-code/models</c>, by walking up from the
/// application folder to the folder that holds <c>models/models.json</c>.
/// </summary>
public static class OcrModels
{
    public static string Root { get; } = FindRoot();

    /// <summary>The folder that holds <c>eng.traineddata</c> for a Tesseract variant: <c>fast</c> or <c>best</c>.</summary>
    public static string Tessdata(string variant)
    {
        var folder = Path.Combine(Root, "tesseract", variant);
        return File.Exists(Path.Combine(folder, "eng.traineddata"))
            ? folder
            : throw new FileNotFoundException($"No Tesseract model in {folder}. Run: python eng/get-models.py tesseract-eng-{variant}");
    }

    /// <summary>PaddleOCR's PP-OCRv5 Latin models, which the RapidOcrNet package copies next to the application.</summary>
    public static string PaddleFolder { get; } = Path.Combine(AppContext.BaseDirectory, "models", "v5");

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "models");
            if (File.Exists(Path.Combine(candidate, "models.json"))) return candidate;
        }

        return Path.Combine(AppContext.BaseDirectory, "models");
    }
}
