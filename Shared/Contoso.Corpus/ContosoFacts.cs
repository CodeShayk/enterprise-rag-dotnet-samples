namespace Contoso.Corpus;

/// <summary>Facts every chapter's code agrees on (Book Bible section 7).</summary>
public static class ContosoFacts
{
    /// <summary>"Today" in all of Contoso's data: Monday, February 1, 2027.</summary>
    public static DateOnly Today { get; } = new(2027, 2, 1);

    /// <summary>The business units whose data is isolated from each other.</summary>
    public static IReadOnlyList<string> Tenants { get; } = ["fluid", "motion", "service"];

    /// <summary>The shared library that isn't a tenant: safety, HR, procurement and IT documents.</summary>
    public const string CorporateLibrary = "corporate";
}
