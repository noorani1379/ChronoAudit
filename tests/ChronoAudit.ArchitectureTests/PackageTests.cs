using System.IO.Compression;
using System.Xml.Linq;
using Xunit;

namespace ChronoAudit.ArchitectureTests;

public sealed class PackageTests
{
    private static readonly string[] s_expectedPackages =
    [
        "ChronoAudit.Core",
        "ChronoAudit.EntityFrameworkCore",
        "ChronoAudit.AspNetCore"
    ];

    private static readonly string[] s_tfms = ["net8.0", "net10.0"];

    [Fact]
    public void ThePackages_HaveTheRequiredContent()
    {
        var repoRoot = RepoPaths.Root;
        var packageDir = Path.Combine(repoRoot, "artifacts", "packages");

        if (!Directory.Exists(packageDir))
        {
            // Skipped when packages haven't been generated yet
            return;
        }

        var nupkgFiles = Directory.GetFiles(packageDir, "*.nupkg");
        if (nupkgFiles.Length == 0)
        {
            return;
        }

        foreach (var pkgName in s_expectedPackages)
        {
            var matchingNupkgs = nupkgFiles
                .Where(f => !f.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase) &&
                            Path.GetFileName(f).StartsWith(pkgName + ".", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.True(matchingNupkgs.Count > 0, $"Expected to find package file for {pkgName} in {packageDir}");
            var nupkgPath = matchingNupkgs[0];

            var snupkgPath = Path.ChangeExtension(nupkgPath, ".snupkg");
            Assert.True(File.Exists(snupkgPath), $"Matching snupkg not found for {nupkgPath}");

            using var zip = ZipFile.OpenRead(nupkgPath);
            var entryNames = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Check README.md
            Assert.True(
                entryNames.Contains("README.md") || entryNames.Any(e => e.EndsWith("/README.md", StringComparison.OrdinalIgnoreCase)),
                $"{pkgName} nupkg is missing README.md");

            // Check icon.png
            Assert.True(
                entryNames.Contains("icon.png") || entryNames.Any(e => e.EndsWith("/icon.png", StringComparison.OrdinalIgnoreCase)),
                $"{pkgName} nupkg is missing icon.png");

            // Check TFMs and XML documentation
            foreach (var tfm in s_tfms)
            {
                var expectedDll = $"lib/{tfm}/{pkgName}.dll";
                var expectedXml = $"lib/{tfm}/{pkgName}.xml";

                Assert.True(entryNames.Contains(expectedDll), $"{pkgName} nupkg is missing {expectedDll}");
                Assert.True(entryNames.Contains(expectedXml), $"{pkgName} nupkg is missing {expectedXml}");
            }

            // Check nuspec metadata
            var nuspecEntry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(nuspecEntry);

            using var stream = nuspecEntry.Open();
            var doc = XDocument.Load(stream);
            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

            var metadata = doc.Root?.Element(ns + "metadata");
            Assert.NotNull(metadata);

            var license = metadata.Element(ns + "license");
            Assert.NotNull(license);
            Assert.Equal("expression", license.Attribute("type")?.Value);
            Assert.Equal("MIT", license.Value.Trim());

            var repository = metadata.Element(ns + "repository");
            Assert.NotNull(repository);
        }
    }
}
