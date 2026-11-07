using System.Text.RegularExpressions;

namespace ChronoAudit.ArchitectureTests;

public class SourceHygieneTests
{
    // Extra words (one per line) live in an untracked file next to the tests, so this public repository never names them.
    private static readonly Regex ForbiddenWords = BuildForbiddenWords();

    private static Regex BuildForbiddenWords()
    {
        var words = new List<string> { "Vehicle", "Car", "Cars", "Plate", "Dossier", "PowerOfAttorney", "Jalali", "Shahkar", "Rahvar", "Bale" };
        var extra = Path.Combine(AppContext.BaseDirectory, "private-words.local.txt");
        if (File.Exists(extra))
        {
            words.AddRange(File.ReadAllLines(extra).Select(l => l.Trim()).Where(l => l.Length > 0));
        }

        return new Regex(@"\b(" + string.Join("|", words.Select(Regex.Escape)) + @")\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    }

    // Arabic, Arabic Supplement, Arabic Presentation Forms A and B, and the zero width non-joiner. Numbers instead of literals on purpose.
    private static readonly (int From, int To)[] ForbiddenRanges =
    [
        (0x0600, 0x06FF), (0x0750, 0x077F), (0xFB50, 0xFDFF), (0xFE70, 0xFEFF), (0x200C, 0x200C),
    ];

    internal static IReadOnlyList<string> Scan(string content)
    {
        var problems = new List<string>();
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            foreach (var ch in line)
            {
                if (ForbiddenRanges.Any(r => ch >= r.From && ch <= r.To))
                {
                    problems.Add($"line {i + 1}: Persian/Arabic character U+{(int)ch:X4} in '{line.Trim()}'");
                    break;
                }
            }

            var match = ForbiddenWords.Match(line);
            if (match.Success)
            {
                problems.Add($"line {i + 1}: forbidden word '{match.Value}' in '{line.Trim()}'");
            }
        }

        return problems;
    }

    [Fact]
    public void TheSource_IsEnglishOnly_AndDomainFree()
    {
        var srcRoot = Path.Combine(RepoPaths.Root, "src");
        var files = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(files);

        var failures = files
            .SelectMany(f => Scan(File.ReadAllText(f)).Select(p => $"{Path.GetRelativePath(RepoPaths.Root, f)}: {p}"))
            .ToList();

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData("var car = 1;", true)]
    [InlineData("class VehicleOwner {}", false)]
    [InlineData("// the Vehicle service", true)]
    [InlineData("Dossier", true)]
    [InlineData("Vehicle.Common", true)]
    [InlineData("Card scar Carpet Plated", false)]
    [InlineData("private const char Yeh = '\\u06CC';", false)]
    public void TheScanner_FlagsWholeWordsOnly_AndPassesUnicodeEscapes(string line, bool flagged)
    {
        Assert.Equal(flagged, Scan(line).Count > 0);
    }

    [Fact]
    public void TheScanner_FlagsALiteralPersianLetter_AndAZwnj()
    {
        Assert.NotEmpty(Scan("var s = \"" + (char)0x06CC + "\";"));
        Assert.NotEmpty(Scan("var s = \"a" + (char)0x200C + "b\";"));
        Assert.Empty(Scan("var s = \"plain ascii\";"));
    }
}
