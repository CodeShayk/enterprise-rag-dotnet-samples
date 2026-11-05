using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Contoso.Corpus;

namespace Contoso.Knowledge.Security;

/// <summary>
/// A stand-in for Contoso's Microsoft Entra ID directory: every group has an object ID, groups nest, and each person is a
/// direct member of a few groups. Documents name the department groups; people belong to teams inside them, so
/// membership has to be expanded through the nesting, as Microsoft Graph's <c>transitiveMemberOf</c> does.
/// </summary>
public sealed class ContosoDirectory
{
    /// <summary>The Entra tenant Contoso's tokens come from; a token from any other tenant is refused.</summary>
    public const string TenantId = "6f1c2a40-3b5d-4c1e-9a7f-2d8e0b4c6a19";

    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _parents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _members = new(StringComparer.Ordinal);

    public static ContosoDirectory Default { get; } = Create();

    private static ContosoDirectory Create()
    {
        var d = new ContosoDirectory();
        foreach (var department in new[] { "fluid", "motion", "service", "procurement", "hr", "people-managers", "knowledge-team" }) d.Nest(department, "employees");
        d.Nest("fluid-night-shift", "fluid");
        d.Nest("fluid-reliability", "fluid");
        d.Nest("service-field", "service");
        d.Nest("procurement-leads", "procurement");
        d.Nest("hr-partners", "hr");
        d.Join(People.Tomas, "fluid-night-shift");
        d.Join(People.Nadia, "fluid-reliability");
        d.Join(People.Kwame, "service-field");
        d.Join(People.Grace, "procurement-leads");
        d.Join(People.Daniel, "hr-partners", "people-managers");
        d.Join(People.Riley, "knowledge-team");
        return d;
    }

    /// <summary>A stable object ID for a group or a person, as Entra would assign once.</summary>
    public static string ObjectId(string name) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes("contoso:" + name))).ToString();

    public string NameOf(string groupId) => _names.TryGetValue(groupId, out var name) ? name : groupId;

    /// <summary>The groups a person belongs to directly, as object IDs: what a token's groups claim may carry.</summary>
    public IReadOnlyList<string> DirectGroups(string personId) => [.. _members.GetValueOrDefault(personId) ?? []];

    /// <summary>Every group a set of groups is inside, directly or through nesting, as names.</summary>
    public IReadOnlySet<string> Expand(IEnumerable<string> groupIds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(groupIds);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (!seen.Add(id)) continue;
            foreach (var parent in _parents.GetValueOrDefault(id) ?? []) pending.Push(parent);
        }

        return seen.Select(NameOf).ToHashSet(StringComparer.Ordinal);
    }

    private void Nest(string child, string parent)
    {
        var (c, p) = (ObjectId(child), ObjectId(parent));
        (_names[c], _names[p]) = (child, parent);
        if (!_parents.TryGetValue(c, out var parents)) _parents[c] = parents = new HashSet<string>(StringComparer.Ordinal);
        parents.Add(p);
    }

    private void Join(Person person, params string[] groups)
    {
        foreach (var group in groups) _names[ObjectId(group)] = group;
        _members[person.Id] = groups.Select(ObjectId).ToHashSet(StringComparer.Ordinal);
    }
}

/// <summary>
/// The asker from a validated access token, never from the request body. Entra puts at most 200 group object IDs in a
/// JWT; past that the token has no groups claim and a <c>_claim_names</c> claim that names it instead, and the app must
/// ask Microsoft Graph. Either way the groups are expanded through the directory's nesting.
/// </summary>
public static class EntraClaims
{
    public static Person ToPerson(ClaimsPrincipal user, ContosoDirectory directory)
    {
        if (user.FindFirst("tid")?.Value != ContosoDirectory.TenantId) throw new UnauthorizedAccessException("The token comes from another tenant.");
        var oid = user.FindFirst("oid")?.Value ?? throw new UnauthorizedAccessException("The token has no object ID.");
        var person = People.All.FirstOrDefault(p => ContosoDirectory.ObjectId(p.Id) == oid) ?? throw new UnauthorizedAccessException("Unknown user.");

        var overage = user.FindFirst("_claim_names")?.Value.Contains("\"groups\"", StringComparison.Ordinal) == true || user.HasClaim(c => c.Type == "hasgroups");
        IEnumerable<string> groups = overage
            ? directory.DirectGroups(person.Id)
            : user.FindAll("groups").Select(c => c.Value);
        return person with { Groups = directory.Expand(groups) };
    }
}
