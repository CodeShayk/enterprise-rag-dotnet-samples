using System.Reflection;
using System.Xml.Linq;
using Ch04.Architecture.Cli;
using Ch04.Knowledge.Adapters;
using Ch04.Knowledge.Core;

namespace Ch04.Tests;

/// <summary>
/// The module boundaries of ADR-0003 (docs/adr), written down as tests so that a pull request
/// can't quietly erase them.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly Assembly Core = typeof(RagPipeline).Assembly;
    private static readonly Assembly Adapters = typeof(VectorIndex).Assembly;
    private static readonly Assembly CompositionRoot = typeof(Composition).Assembly;

    [Fact]
    public void The_core_uses_only_the_base_library_and_the_domain()
    {
        var used = Core.GetReferencedAssemblies().Select(a => a.Name!);

        Assert.All(used, name => Assert.True(
            name == "Contoso.Corpus" || name.StartsWith("System", StringComparison.Ordinal),
            $"Ch04.Knowledge.Core uses {name}"));
    }

    [Fact]
    public void The_core_project_references_no_packages_and_only_the_domain()
    {
        // Catches references the compiler would drop because nothing uses them yet.
        var project = XDocument.Load(ProjectFile("Ch04.Knowledge.Core"));

        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Equal(new[] { "Contoso.Corpus.csproj" },
            project.Descendants("ProjectReference").Select(r => Path.GetFileName((string)r.Attribute("Include")!)));
    }

    [Fact]
    public void Every_port_has_at_least_one_adapter()
    {
        var ports = Core.GetTypes().Where(t => t.IsInterface && t.Namespace == "Ch04.Knowledge.Core.Ports").ToList();

        Assert.NotEmpty(ports);
        Assert.All(ports, port => Assert.Contains(Adapters.GetTypes(), t => t is { IsClass: true, IsAbstract: false } && port.IsAssignableFrom(t)));
    }

    [Fact]
    public void Adapters_know_nothing_about_hosts()
    {
        var used = Adapters.GetReferencedAssemblies().Select(a => a.Name!);

        Assert.DoesNotContain(used, name => name == CompositionRoot.GetName().Name
            || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal));
    }

    private static string ProjectFile(string project)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Contoso-Knowledge.slnx")))
        {
            folder = folder.Parent;
        }

        return Path.Combine(folder?.FullName ?? throw new DirectoryNotFoundException("sample-code"),
            "Chapter04-Architecture", project, $"{project}.csproj");
    }
}
