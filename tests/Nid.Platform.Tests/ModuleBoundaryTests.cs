using System.Xml.Linq;

namespace Nid.Platform.Tests;

/// <summary>
/// The module boundaries of <c>docs/architecture.md</c> §5.1, read from the <c>ProjectReference</c> and <c>PackageReference</c>
/// items of <c>src/*/*/*.csproj</c>: a forbidden reference fails here before any code uses it. CI runs this class on every pull request.
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
    public void A_module_references_only_its_own_projects_the_shared_ones_and_other_modules_contracts()
    {
        Assert.Contains("Subscriptions", Modules);
        NoViolation(
            "A module references only its own projects and " + string.Join(", ", Shared)
                + "; only its Application may reference another module's Contracts",
            from p in All
            let module = ModuleOf(p.Name)
            where module is not null
            from r in p.Projects
            where !r.StartsWith($"Nid.{module}.", StringComparison.Ordinal) && !Shared.Contains(r) && !ReadsContracts(p.Name, r)
            select $"{p.Name} -> {r}");
    }

    [Fact]
    public void Modules_reading_each_others_contracts_form_no_cycle()
    {
        var edges = (from p in All
                     let source = ModuleOf(p.Name)
                     where source is not null
                     from r in p.Projects
                     let target = ModuleOf(r)
                     where target is not null && target != source
                     select (source, target)).Distinct().ToArray();
        NoViolation(
            "Modules reading each other's Contracts form no cycle",
            from m in Modules
            where Reachable(m, edges).Contains(m)
            select $"{m} -> ... -> {m} ({string.Join(", ", edges.Select(e => $"{e.source} -> {e.target}"))})");
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
            "Only " + string.Join(" and ", CompositionRoots) + " reference a module's projects from outside that module, "
                + "besides another module's Application reading its Contracts",
            from p in All
            where !CompositionRoots.Contains(p.Name)
            from r in p.Projects
            let module = ModuleOf(r)
            where module is not null && module != ModuleOf(p.Name) && !ReadsContracts(p.Name, r)
            select $"{p.Name} -> {r}");
    }

    /// <summary>§5.1: a contract one module reads from another lives in the providing module's Contracts.</summary>
    private static bool ReadsContracts(string project, string reference)
        => project.EndsWith(".Application", StringComparison.Ordinal)
           && reference.EndsWith(".Contracts", StringComparison.Ordinal)
           && ModuleOf(project) is not null && ModuleOf(reference) is not null;

    private static HashSet<string> Reachable(string module, (string source, string target)[] edges)
    {
        var seen = new HashSet<string>();
        var next = new Queue<string>([module]);
        while (next.TryDequeue(out var m))
            foreach (var (_, to) in edges.Where(e => e.source == m))
                if (seen.Add(to))
                    next.Enqueue(to);
        return seen;
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
        => Directory.EnumerateDirectories(Path.Combine(Vectors.RepositoryRoot(), "src")).SelectMany(Directory.EnumerateDirectories)
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
