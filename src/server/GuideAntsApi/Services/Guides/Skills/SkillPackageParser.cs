using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using AntRunner.ToolCalling.AssistantDefinitions;
using GuideAntsApi.Models.Guides;

namespace GuideAntsApi.Services.Guides.Skills;

public sealed class SkillPackageParser
{
    private static readonly Regex SafeSkillNamePattern = new(@"^[a-z0-9][a-z0-9_-]*$", RegexOptions.IgnoreCase);

    public ParsedSkillPackage ParseFolderEntries(
        IReadOnlyDictionary<string, byte[]> entries,
        string source = "Imported")
    {
        if (entries.Count == 0)
        {
            throw new InvalidOperationException("Skill package is empty.");
        }

        var normalized = NormalizeEntries(entries);
        var skillRoot = ResolveSkillRoot(normalized);
        var skillMarkdownPath = FindSkillMarkdown(normalized, skillRoot);
        var originalMarkdown = Encoding.UTF8.GetString(normalized[skillMarkdownPath]);
        var frontmatter = SkillFrontmatter.Parse(originalMarkdown);

        ValidateSkillName(frontmatter.Name);

        var skillFolder = $"Skills/{frontmatter.Name}";
        var files = new List<FileUploadDto>();

        foreach (var (relativePath, content) in normalized)
        {
            if (!IsUnderSkillRoot(relativePath, skillRoot))
            {
                continue;
            }

            var packageRelative = StripSkillRoot(relativePath, skillRoot);
            if (string.IsNullOrWhiteSpace(packageRelative))
            {
                continue;
            }

            var targetPath = SkillPathSafety.NormalizePath($"{skillFolder}/{packageRelative}");
            ValidateSkillRelativePath(targetPath, skillFolder);

            var uploadBytes = string.Equals(packageRelative, "SKILL.md", StringComparison.OrdinalIgnoreCase)
                ? Encoding.UTF8.GetBytes(originalMarkdown)
                : content;

            files.Add(new FileUploadDto(
                "Skill",
                null,
                targetPath,
                uploadBytes,
                GuessContentType(packageRelative)));
        }

        EnsureSingleSkillManifest(files, skillFolder);

        return new ParsedSkillPackage(
            frontmatter,
            originalMarkdown,
            source,
            files);
    }

    public ParsedSkillPackage ParseZip(Stream zipStream, string source = "Imported")
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            using var entryStream = entry.Open();
            using var memory = new MemoryStream();
            entryStream.CopyTo(memory);
            entries[entry.FullName.Replace('\\', '/')] = memory.ToArray();
        }

        return ParseFolderEntries(entries, source);
    }

    private static Dictionary<string, byte[]> NormalizeEntries(IReadOnlyDictionary<string, byte[]> entries)
    {
        var normalized = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, content) in entries)
        {
            var key = SkillPathSafety.NormalizePath(path);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (key.Split('/').Any(segment => segment == ".."))
            {
                throw new InvalidOperationException($"Path traversal is not allowed: '{path}'.");
            }

            normalized[key] = content;
        }

        return normalized;
    }

    private static string ResolveSkillRoot(Dictionary<string, byte[]> entries)
    {
        var manifests = entries.Keys
            .Where(path => path.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(path, "SKILL.md", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (manifests.Count == 0)
        {
            throw new InvalidOperationException("Skill package must contain a SKILL.md file.");
        }

        if (manifests.Count > 1)
        {
            throw new InvalidOperationException(
                "Skill package must contain exactly one SKILL.md file at the package root.");
        }

        var manifestPath = manifests[0];
        var slashIndex = manifestPath.LastIndexOf('/');
        return slashIndex < 0 ? string.Empty : manifestPath[..slashIndex];
    }

    private static string FindSkillMarkdown(Dictionary<string, byte[]> entries, string skillRoot)
    {
        var manifestPath = string.IsNullOrEmpty(skillRoot) ? "SKILL.md" : $"{skillRoot}/SKILL.md";
        if (!entries.ContainsKey(manifestPath))
        {
            throw new InvalidOperationException("Skill package must contain a SKILL.md file.");
        }

        return manifestPath;
    }

    private static bool IsUnderSkillRoot(string path, string skillRoot)
    {
        if (string.IsNullOrEmpty(skillRoot))
        {
            return true;
        }

        return path.Equals(skillRoot, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(skillRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripSkillRoot(string path, string skillRoot)
    {
        if (string.IsNullOrEmpty(skillRoot))
        {
            return path;
        }

        if (path.Equals(skillRoot, StringComparison.OrdinalIgnoreCase))
        {
            return "SKILL.md";
        }

        return path[(skillRoot.Length + 1)..];
    }

    private static void ValidateSkillRelativePath(string targetPath, string skillFolder)
    {
        var normalizedFolder = SkillPathSafety.NormalizePath(skillFolder);
        if (!targetPath.StartsWith(normalizedFolder + "/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Skill file path '{targetPath}' must stay under '{normalizedFolder}/'.");
        }
    }

    private static void EnsureSingleSkillManifest(List<FileUploadDto> files, string skillFolder)
    {
        var manifestCount = files.Count(file =>
            file.RelativePath.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase));
        if (manifestCount != 1)
        {
            throw new InvalidOperationException(
                $"Skill folder '{skillFolder}' must contain exactly one SKILL.md file.");
        }
    }

    private static void ValidateSkillName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("SKILL.md frontmatter is missing required field 'name'.");
        }

        if (!SafeSkillNamePattern.IsMatch(name))
        {
            throw new InvalidOperationException(
                $"Skill name '{name}' is not safe for a locator segment.");
        }
    }

    private static string? GuessContentType(string relativePath)
    {
        if (relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return "text/markdown";
        }

        if (relativePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
        {
            return "text/x-python";
        }

        if (relativePath.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
        {
            return "text/x-shellscript";
        }

        return null;
    }
}

public sealed record ParsedSkillPackage(
    SkillFrontmatter Frontmatter,
    string OriginalSkillMarkdown,
    string Source,
    List<FileUploadDto> Files);
