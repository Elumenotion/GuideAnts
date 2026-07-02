using System.Text;
using FluentAssertions;
using GuideAntsApi.Services.Guides.Skills;

namespace GuideAntsApi.Tests.Services.Skills;

[TestClass]
public sealed class SkillPackageParserTests
{
    private const string AgentskillsMarkdown = """
---
name: pptx-author
description: Build export-ready PowerPoint decks.
metadata:
  guideants:
    enabled: true
    display_order: 10
    requires_toolsets: [sandbox]
---
# Body
""";

    private const string HermesMarkdown = """
---
name: hermes-skill
description: Hermes dialect skill.
metadata:
  hermes:
    enabled: true
    display_order: 5
    requires_toolsets: [web]
---
Body text.
""";

    private const string ClaudeMarkdown = """
---
name: claude-skill
description: Claude Code dialect skill.
allowed-tools:
  - Bash
argument-hint: "[topic]"
---
Slash command body.
""";

    private readonly SkillPackageParser _parser = new();

    [TestMethod]
    public void ParseFolderEntries_AgentskillsDialect_PreservesOriginalSkillMarkdown()
    {
        var entries = new Dictionary<string, byte[]>
        {
            ["SKILL.md"] = Encoding.UTF8.GetBytes(AgentskillsMarkdown),
            ["references/guide.md"] = Encoding.UTF8.GetBytes("# Ref"),
        };

        var result = _parser.ParseFolderEntries(entries);

        result.Frontmatter.Name.Should().Be("pptx-author");
        result.Files.Should().ContainSingle(f =>
            f.RelativePath == "Skills/pptx-author/SKILL.md"
            && Encoding.UTF8.GetString(f.ContentBytes) == AgentskillsMarkdown);
        result.Files.Should().Contain(f => f.RelativePath == "Skills/pptx-author/references/guide.md");
    }

    [TestMethod]
    public void ParseFolderEntries_HermesDialect_NormalizesMetadata()
    {
        var entries = new Dictionary<string, byte[]>
        {
            ["skill/SKILL.md"] = Encoding.UTF8.GetBytes(HermesMarkdown),
        };

        var result = _parser.ParseFolderEntries(entries);

        result.Frontmatter.Name.Should().Be("hermes-skill");
        result.Frontmatter.RequiresToolsets.Should().Contain("web");
    }

    [TestMethod]
    public void ParseFolderEntries_ClaudeCodeDialect_AcceptsAllowedTools()
    {
        var entries = new Dictionary<string, byte[]>
        {
            ["SKILL.md"] = Encoding.UTF8.GetBytes(ClaudeMarkdown),
        };

        var result = _parser.ParseFolderEntries(entries);

        result.Frontmatter.Name.Should().Be("claude-skill");
        result.Frontmatter.RequiresTools.Should().Contain("Bash");
    }

    [TestMethod]
    public void ParseFolderEntries_MissingSkillMarkdown_Throws()
    {
        var act = () => _parser.ParseFolderEntries(new Dictionary<string, byte[]>
        {
            ["readme.md"] = Encoding.UTF8.GetBytes("nope"),
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SKILL.md*");
    }

    [TestMethod]
    public void ParseFolderEntries_MissingName_Throws()
    {
        var act = () => _parser.ParseFolderEntries(new Dictionary<string, byte[]>
        {
            ["SKILL.md"] = Encoding.UTF8.GetBytes("""
---
description: no name
---
body
"""),
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*name*");
    }

    [TestMethod]
    public void ParseZip_ProducesSameFilesAsFolder()
    {
        using var zipStream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("SKILL.md");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(AgentskillsMarkdown);
        }

        zipStream.Position = 0;
        var result = _parser.ParseZip(zipStream);

        result.Frontmatter.Name.Should().Be("pptx-author");
        result.Files.Should().Contain(f => f.FolderKind == "Skill");
    }
}
