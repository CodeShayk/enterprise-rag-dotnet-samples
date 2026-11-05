namespace Contoso.Corpus;

/// <summary>Someone who asks the assistant questions, and the groups their access comes from.</summary>
public sealed record Person(string Id, string Name, string Role, string Tenant, IReadOnlySet<string> Groups)
{
    /// <summary>Whether this person belongs to at least one of the document's allowed groups.</summary>
    public bool CanRead(SourceDocument document) => document.AllowedGroups.Any(Groups.Contains);
}

/// <summary>The people who use the Contoso Knowledge Assistant throughout the book.</summary>
public static class People
{
    public static Person Nadia { get; } = new("nadia.rahman", "Nadia Rahman", "Reliability engineer", "fluid",
        new HashSet<string> { "employees", "fluid" });

    public static Person Tomas { get; } = new("tomas.silva", "Tomás Silva", "Maintenance technician, night shift", "fluid",
        new HashSet<string> { "employees", "fluid" });

    public static Person Kwame { get; } = new("kwame.asante", "Kwame Asante", "Field service engineer", "service",
        new HashSet<string> { "employees", "service" });

    public static Person Grace { get; } = new("grace.liu", "Grace Liu", "Procurement lead", ContosoFacts.CorporateLibrary,
        new HashSet<string> { "employees", "procurement" });

    public static Person Daniel { get; } = new("daniel.mensah", "Daniel Mensah", "HR business partner", ContosoFacts.CorporateLibrary,
        new HashSet<string> { "employees", "hr", "people-managers" });

    public static Person Riley { get; } = new("riley.chen", "Riley Chen", "Staff engineer, Knowledge Assistant team", ContosoFacts.CorporateLibrary,
        new HashSet<string> { "employees", "knowledge-team" });

    public static IReadOnlyList<Person> All { get; } = [Nadia, Tomas, Kwame, Grace, Daniel, Riley];

    /// <summary>The person with this ID, such as <c>tomas.silva</c>, or <see langword="null"/>.</summary>
    public static Person? Find(string id) => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
