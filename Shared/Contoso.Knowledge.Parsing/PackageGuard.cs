using System.IO.Compression;
using Contoso.Knowledge.Documents;

namespace Contoso.Knowledge.Parsing;

/// <summary>
/// Checks a zip-based file (Word, PowerPoint, Excel) before any parser opens it: the expanded size and the
/// compression ratio stop zip bombs, and a VBA project means macros, which the pipeline refuses.
/// </summary>
public static class PackageGuard
{
    /// <summary>A real Office file rarely compresses better than this; zip bombs compress far better.</summary>
    public const double MaxCompressionRatio = 100;

    public static bool IsZipPackage(string mediaType) =>
        mediaType is MediaTypes.Docx or MediaTypes.Pptx or MediaTypes.Xlsx;

    public static void Check(Stream package, ParserLimits limits)
    {
        try
        {
            using var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
            long expanded = 0;
            foreach (var entry in zip.Entries)
            {
                // Declared sizes can lie: the Word parser also caps the characters in each part, and the registry's timeout bounds the rest.
                expanded += entry.Length;
                if (expanded > limits.MaxExpandedBytes)
                {
                    throw new ParserLimitException($"The package expands to more than {limits.MaxExpandedBytes / 1024 / 1024} MB.");
                }

                if (entry.CompressedLength > 0 && (double)entry.Length / entry.CompressedLength > MaxCompressionRatio && entry.Length > 1024 * 1024)
                {
                    throw new ParserLimitException($"{entry.FullName} compresses {entry.Length / entry.CompressedLength}:1, which looks like a zip bomb.");
                }

                if (entry.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ParserLimitException("The file contains macros (a VBA project).");
                }
            }
        }
        catch (InvalidDataException e)
        {
            throw new ParserLimitException($"The file isn't a valid zip package: {e.Message}");
        }
    }
}
