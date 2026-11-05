using System.Diagnostics;

namespace Contoso.Knowledge.Parsing;

/// <summary>
/// Converts legacy binary Office files (<c>.doc</c>, <c>.xls</c>, <c>.ppt</c>) to their Open XML successors with
/// LibreOffice in headless mode, so that the modern parsers can read them. The converter is a separate process with a
/// throwaway profile and working folder, a hard timeout and its whole process tree killed on expiry: a crafted legacy
/// file attacks LibreOffice, not the pipeline. Run it in a container or a low-privilege account in production.
/// </summary>
public sealed class LegacyConverter(string? sofficePath = null)
{
    private static readonly Dictionary<string, string> Targets = new(StringComparer.OrdinalIgnoreCase)
    {
        [".doc"] = "docx", [".xls"] = "xlsx", [".ppt"] = "pptx", [".rtf"] = "docx",
    };

    /// <summary>The LibreOffice executable, from the constructor, the PATH or the default install folders; null if none.</summary>
    public string? Soffice { get; } = sofficePath ?? Find();

    public static bool CanConvert(string fileName) => Targets.ContainsKey(Path.GetExtension(fileName));

    public async Task<byte[]> ConvertAsync(byte[] content, string fileName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (Soffice is null) throw new UnsupportedFormatException("LibreOffice isn't installed, so legacy Office files can't be converted.");
        var target = Targets[Path.GetExtension(fileName)];

        var work = Directory.CreateTempSubdirectory("contoso-legacy-");
        try
        {
            var input = Path.Combine(work.FullName, "input" + Path.GetExtension(fileName));
            await File.WriteAllBytesAsync(input, content, cancellationToken);
            var start = new ProcessStartInfo(Soffice)
            {
                // A private profile per conversion: no shared state, no macros from a user profile, no first-start dialogs.
                ArgumentList = { $"-env:UserInstallation=file:///{work.FullName.Replace('\\', '/')}/profile", "--headless", "--norestore",
                                 "--nolockcheck", "--convert-to", target, "--outdir", work.FullName, input },
                WorkingDirectory = work.FullName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(start) ?? throw new InvalidOperationException("LibreOffice didn't start.");
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new ParserLimitException($"{fileName}: conversion took longer than {timeout.TotalSeconds:N0} s.");
            }

            var output = Path.Combine(work.FullName, "input." + target);
            return File.Exists(output)
                ? await File.ReadAllBytesAsync(output, cancellationToken)
                : throw new DocumentParseException($"{fileName}: LibreOffice produced no output.", new InvalidDataException(await process.StandardError.ReadToEndAsync(cancellationToken)));
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    private static string? Find()
    {
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .SelectMany(d => new[] { Path.Combine(d, "soffice.exe"), Path.Combine(d, "soffice") })
            .Concat([@"C:\Program Files\LibreOffice\program\soffice.exe", "/usr/bin/soffice", "/Applications/LibreOffice.app/Contents/MacOS/soffice"]);
        return candidates.FirstOrDefault(File.Exists);
    }
}
