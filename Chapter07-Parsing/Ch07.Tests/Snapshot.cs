using System.Runtime.CompilerServices;

namespace Ch07.Tests;

/// <summary>
/// Chapter 3's snapshot helper with a name per case, so that one theory can keep a snapshot per file. The approved
/// output lives in <c>Snapshots/name.verified.txt</c>; anything else is written to <c>name.received.txt</c> and fails
/// the test. Review the difference, then fix the parser or accept the output by renaming the file.
/// </summary>
public static class Snapshot
{
    public static void Match(string actual, string name, [CallerFilePath] string testFile = "")
    {
        var folder = Path.Combine(Path.GetDirectoryName(testFile)!, "Snapshots");
        Directory.CreateDirectory(folder);
        var verified = Path.Combine(folder, name + ".verified.txt");
        var received = Path.Combine(folder, name + ".received.txt");
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
