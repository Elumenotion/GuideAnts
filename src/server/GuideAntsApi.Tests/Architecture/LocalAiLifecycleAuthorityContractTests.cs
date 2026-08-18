using FluentAssertions;
using System.Text.RegularExpressions;

namespace GuideAntsApi.Tests.Architecture;

[TestClass]
public sealed partial class LocalAiLifecycleAuthorityContractTests
{
    [TestMethod]
    public void RuntimeAndConfiguration_DoNotContainRetiredLifecycleAuthorities()
    {
        var repoRoot = FindRepositoryRoot();
        var roots = new[]
        {
            Path.Combine(repoRoot, "docker"),
            Path.Combine(repoRoot, "installer", "docker"),
            Path.Combine(repoRoot, "src", "server", "GuideAntsApi"),
        };

        var offenders = EnumerateContractFiles(roots)
            .SelectMany(path => FindForbiddenTerms(repoRoot, path))
            .ToList();

        offenders.Should().BeEmpty(
            "container autoload, persisted desired plans, and engine-to-ServiceModes backfill are retired: {0}",
            string.Join(Environment.NewLine, offenders));
    }

    [TestMethod]
    public void CurrentDocumentation_DoesNotTeachRetiredLifecycleAuthorities()
    {
        var repoRoot = FindRepositoryRoot();
        var roots = new[]
        {
            Path.Combine(repoRoot, "docs"),
            Path.Combine(repoRoot, "docker"),
        };

        var offenders = EnumerateContractFiles(roots)
            .Where(path => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}_archive{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => FindForbiddenTerms(repoRoot, path))
            .ToList();

        offenders.Should().BeEmpty(
            "current documentation must describe GuideAntsApi as the sole lifecycle authority: {0}",
            string.Join(Environment.NewLine, offenders));
    }

    private static IEnumerable<string> FindForbiddenTerms(string repoRoot, string path)
    {
        var text = File.ReadAllText(path);
        var relativePath = Path.GetRelativePath(repoRoot, path);
        foreach (var (name, pattern) in ForbiddenPatterns())
        {
            if (pattern.IsMatch(text))
            {
                yield return $"{relativePath}: {name}";
            }
        }
    }

    private static IEnumerable<(string Name, Regex Pattern)> ForbiddenPatterns()
    {
        yield return (
            "auxiliary startup autoload variable",
            AuxiliaryAutoloadVariableRegex());
        yield return (
            "persisted desired INI",
            new Regex("warmup-" + "desired\\.ini|warmup_" + "desired_ini", RegexOptions.IgnoreCase));
        yield return (
            "engine-to-ServiceModes synchronization",
            new Regex("ConfiguredLocalServiceSelection" + "Sync", RegexOptions.IgnoreCase));
        yield return (
            "split desired/apply HTTP protocol",
            new Regex("warmup/" + "desired", RegexOptions.IgnoreCase));
    }

    private static IEnumerable<string> EnumerateContractFiles(IEnumerable<string> roots)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".py", ".ps1", ".sh", ".yml", ".yaml", ".env", ".example", ".md", ".tsx",
        };

        return roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(path => extensions.Contains(Path.GetExtension(path)))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}_archive{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith(
                nameof(LocalAiLifecycleAuthorityContractTests) + ".cs",
                StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "docker"))
                && Directory.Exists(Path.Combine(current.FullName, "src")))
            {
                return current.FullName;
            }
        }

        throw new InvalidOperationException("Unable to locate repository root.");
    }

    [GeneratedRegex(
        "GA_(ASR|TTS|EMB|SD)_AUTO_" + "LOAD_ON_STARTUP",
        RegexOptions.IgnoreCase)]
    private static partial Regex AuxiliaryAutoloadVariableRegex();
}
