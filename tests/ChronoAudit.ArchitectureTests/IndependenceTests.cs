using System.Reflection;
using System.Xml.Linq;

namespace ChronoAudit.ArchitectureTests;

public class IndependenceTests
{
    private static readonly string[] CorePackages =
    [
        "Microsoft.SourceLink.GitHub",
        "Microsoft.CodeAnalysis.PublicApiAnalyzers",
    ];

    private static readonly string[] EfPackages =
    [
        .. CorePackages,
        "Microsoft.EntityFrameworkCore.Relational",
        "Microsoft.Extensions.Hosting.Abstractions",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.Options",
        "Microsoft.Extensions.Configuration.Abstractions",
    ];

    private static readonly string[] AspNetPackages =
    [
        .. CorePackages,
        "Microsoft.AspNetCore.App",
    ];

    private static readonly Dictionary<string, string[]> AllowedByProject = new()
    {
        ["ChronoAudit.Core"] = CorePackages,
        ["ChronoAudit.EntityFrameworkCore"] = EfPackages,
        ["ChronoAudit.AspNetCore"] = AspNetPackages,
    };

    [Fact]
    public void OnlyAllowedPackages_AreReferenced()
    {
        var srcRoot = Path.Combine(RepoPaths.Root, "src");
        var buildProps = Included(Path.Combine(RepoPaths.Root, "Directory.Build.props"));
        var projects = Directory.GetFiles(srcRoot, "*.csproj", SearchOption.AllDirectories);
        Assert.Equal(AllowedByProject.Count, projects.Length);

        foreach (var csproj in projects)
        {
            var name = Path.GetFileNameWithoutExtension(csproj);
            Assert.True(AllowedByProject.TryGetValue(name, out var allowed), $"Unexpected project {name}");

            var referenced = Included(csproj).Concat(buildProps).Distinct().ToList();
            var forbidden = referenced.Except(allowed!).ToList();
            Assert.True(forbidden.Count == 0, $"{name} references forbidden packages: {string.Join(", ", forbidden)}");

            var projectDir = Path.GetDirectoryName(csproj)!;
            var doc = XDocument.Load(csproj);
            foreach (var reference in doc.Descendants("ProjectReference"))
            {
                var target = Path.GetFullPath(Path.Combine(projectDir, reference.Attribute("Include")!.Value.Replace('\\', Path.DirectorySeparatorChar)));
                Assert.StartsWith(Path.GetFullPath(srcRoot), target, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void PublicTypes_LiveInTheirPackageNamespace()
    {
        var shared = new[] { "Microsoft.Extensions.DependencyInjection", "Microsoft.AspNetCore.Builder", "Microsoft.EntityFrameworkCore.Migrations" };
        var assemblies = new (Assembly Assembly, string Namespace)[]
        {
            (typeof(ChronoAuditInfo).Assembly, "ChronoAudit"),
            (Assembly.Load("ChronoAudit.EntityFrameworkCore"), "ChronoAudit.EntityFrameworkCore"),
            (Assembly.Load("ChronoAudit.AspNetCore"), "ChronoAudit.AspNetCore"),
        };

        foreach (var (assembly, ns) in assemblies)
        {
            foreach (var type in assembly.GetExportedTypes())
            {
                var typeNs = type.Namespace ?? string.Empty;
                Assert.True(
                    typeNs == ns || shared.Contains(typeNs),
                    $"{type.FullName} in {assembly.GetName().Name} is in namespace '{typeNs}', expected '{ns}' or one of {string.Join(", ", shared)}");
            }
        }
    }

    [Fact]
    public void PublicApiFiles_Exist()
    {
        var srcRoot = Path.Combine(RepoPaths.Root, "src");
        var folders = Directory.GetDirectories(srcRoot);
        Assert.Equal(AllowedByProject.Count, folders.Length);

        foreach (var folder in folders)
        {
            Assert.True(File.Exists(Path.Combine(folder, "PublicAPI.Shipped.txt")), $"{folder} lacks PublicAPI.Shipped.txt");
            Assert.True(File.Exists(Path.Combine(folder, "PublicAPI.Unshipped.txt")), $"{folder} lacks PublicAPI.Unshipped.txt");
        }
    }

    private static List<string> Included(string file) =>
        XDocument.Load(file)
            .Descendants()
            .Where(e => e.Name.LocalName is "PackageReference" or "FrameworkReference")
            .Select(e => e.Attribute("Include")?.Value)
            .OfType<string>()
            .ToList();
}
