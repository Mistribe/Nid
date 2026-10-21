using System.Xml.Linq;

namespace Nid.Platform.Tests;

/// <summary>
/// The module boundaries of <c>docs/architecture.md</c> §5.1, read from the <c>ProjectReference</c> and <c>PackageReference</c>
/// items of <c>src/*/*.csproj</c>: a forbidden reference fails here before any code uses it. CI runs this class on every pull request.
/// </summary>
public class ModuleBoundaryTests
{
    private sealed record Project(string Name, string[] Projects, string[] Packages);

    private static readonly Project[] All = Load();

    /// <summary>A business module is a <c>Nid.X</c> with a Domain project; Core is the shared kernel, not one of them.</summary>
    private static readonly string[] Modules = All
        .Where(p => p.Name.EndsWith(".Domain", StringComparison.Ordinal))
        .Select(p => p.Name.Split('.')[1])
        .Where(m => m != "Core")
        .ToArray();

    private static readonly string[] Shared = ["Nid.Core.Contracts", "Nid.Platform", "Nid.Platform.Contracts", "Nid.Platform.Infrastructure"];

    private static readonly string[] CompositionRoots = ["Nid.Host", "Nid.AppHost"];

    [Fact]
    public void A_module_references_only_its_own_projects_and_the_shared_ones()
    {
        Assert.Contains("Subscriptions", Modules);
        NoViolation(
            "A module references only its own projects and " + string.Join(", ", Shared),
            from p in All
            let module = ModuleOf(p.Name)
            where module is not null
            from r in p.Projects
            where !r.StartsWith($"Nid.{module}.", StringComparison.Ordinal) && !Shared.Contains(r)
            select $"{p.Name} -> {r}");
    }

    [Fact]
    public void A_domain_references_only_its_own_contracts_and_no_package()
    {
        var domains = All.Where(p => p.Name.EndsWith(".Domain", StringComparison.Ordinal)).ToArray();
        NoViolation(
            "A Domain project references only its own Contracts and no package (no Synapse, no EF Core)",
            (from p in domains
             from r in p.Projects
             where r != p.Name[..^".Domain".Length] + ".Contracts"
             select $"{p.Name} -> {r}")
            .Concat(
             from p in domains
             from package in p.Packages
             select $"{p.Name} -> package {package}"));
    }

    [Fact]
    public void Contracts_reference_no_domain_application_or_infrastructure()
    {
        string[] layers = [".Domain", ".Application", ".Infrastructure"];
        NoViolation(
            "A Contracts project references no Domain, Application or Infrastructure project",
            from p in All
            where p.Name.EndsWith(".Contracts", StringComparison.Ordinal)
            from r in p.Projects
            where layers.Any(l => r.EndsWith(l, StringComparison.Ordinal))
            select $"{p.Name} -> {r}");
    }

    [Fact]
    public void Only_the_host_references_a_module_from_outside_it()
    {
        NoViolation(
            "Only " + string.Join(" and ", CompositionRoots) + " reference a module's projects from outside that module",
            from p in All
            where !CompositionRoots.Contains(p.Name)
            from r in p.Projects
            let module = ModuleOf(r)
            where module is not null && module != ModuleOf(p.Name)
            select $"{p.Name} -> {r}");
    }

    private static string? ModuleOf(string project)
    {
        var parts = project.Split('.');
        return parts.Length > 2 && Modules.Contains(parts[1]) ? parts[1] : null;
    }

    private static void NoViolation(string rule, IEnumerable<string> violations)
    {
        var found = violations.ToArray();
        Assert.True(found.Length == 0, $"{rule}. Forbidden:{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", found)}");
    }

    private static Project[] Load()
        => Directory.EnumerateDirectories(Path.Combine(Vectors.RepositoryRoot(), "src"))
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.csproj"))
            .Select(path =>
            {
                var xml = XDocument.Load(path);
                // Include paths are written with backslashes, which Path does not split on Linux.
                return new Project(
                    Path.GetFileNameWithoutExtension(path),
                    Includes(xml, "ProjectReference").Select(i => Path.GetFileNameWithoutExtension(i.Replace('\\', '/'))).ToArray(),
                    Includes(xml, "PackageReference").ToArray());
            })
            .ToArray();

    private static IEnumerable<string> Includes(XDocument xml, string item)
        => xml.Descendants(item).Select(e => (string?)e.Attribute("Include")).OfType<string>();
}
