using System.Runtime.CompilerServices;

namespace Ch03.Tests;

/// <summary>
/// Snapshot testing in miniature, in the way Verify works: the approved output lives next to the
/// test as <c>File.Test.verified.txt</c>. When a test produces anything else, the helper writes
/// <c>File.Test.received.txt</c> beside it and fails. You review the difference, then fix the
/// code or accept the new output by renaming the received file.
/// </summary>
public static class Snapshot
{
    public static void Match(string actual, [CallerFilePath] string testFile = "", [CallerMemberName] string testName = "")
    {
        var stem = Path.Combine(Path.GetDirectoryName(testFile)!, $"{Path.GetFileNameWithoutExtension(testFile)}.{testName}");
        var verified = stem + ".verified.txt";
        var received = stem + ".received.txt";
        actual = actual.ReplaceLineEndings("\n");

        if (File.Exists(verified) && File.ReadAllText(verified).ReplaceLineEndings("\n") == actual)
        {
            File.Delete(received);
            return;
        }

        File.WriteAllText(received, actual);
        Assert.Fail($"The output doesn't match {Path.GetFileName(verified)}. Review {Path.GetFileName(received)}; " +
                    "if it's right, rename it to .verified.txt and commit it.");
    }
}
