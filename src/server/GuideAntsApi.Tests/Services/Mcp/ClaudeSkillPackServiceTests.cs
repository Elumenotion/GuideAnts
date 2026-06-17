using System.IO.Compression;
using System.Text;
using FluentAssertions;
using GuideAntsApi.Services.Mcp;

namespace GuideAntsApi.Tests.Services.Mcp;

[TestClass]
public sealed class ClaudeSkillPackServiceTests
{
    private static readonly Guid PubId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string ApiBase = "https://app.example.com/api";

    [TestMethod]
    public void BuildSkillDirectoryName_Sanitizes_and_lowercases()
    {
        ClaudeSkillPackService.BuildSkillDirectoryName("Creative Guide", "Fallback")
            .Should().Be("creative-guide");
    }

    [TestMethod]
    public void BuildToolReferenceTable_Lists_assistants()
    {
        var roster = McpAssistantToolNaming.AssignToolNames(
        [
            (Guid.NewGuid(), "Creative Guide", "Main guide", true),
            (Guid.NewGuid(), "Copy Editor", "Edits copy", false)
        ]);

        var table = ClaudeSkillPackService.BuildToolReferenceTable(roster);

        table.Should().Contain("Creative_Guide");
        table.Should().Contain("Copy_Editor");
        table.Should().Contain("| Guide |");
        table.Should().Contain("| Crew |");
    }

    [TestMethod]
    public async Task BuildAsync_Produces_expected_zip_entries()
    {
        var roster = McpAssistantToolNaming.AssignToolNames(
        [
            (PubId, "Architecture Guide", "Design reviews", true)
        ]);

        var service = new ClaudeSkillPackService();
        var result = await service.BuildAsync(new ClaudeSkillPackBuildRequest(
            PubId,
            "Architecture Guide",
            "architecture-guide",
            "Reviews system designs.",
            ApiBase,
            roster));

        result.FileName.Should().Be("architecture-guide-claude-skill.zip");
        result.SkillDirectoryName.Should().Be("architecture-guide");

        using var zipStream = new MemoryStream(result.ZipBytes);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        var names = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
        names.Should().Contain("architecture-guide/SKILL.md");
        names.Should().Contain("architecture-guide/reference.md");
        names.Should().Contain("architecture-guide/README.md");
        names.Should().Contain("architecture-guide/.env");
        names.Should().Contain("architecture-guide/.env.example");
        names.Should().Contain("architecture-guide/scripts/guideants_mcp.py");

        // The pack is Python-only; no bash scripts should ship.
        names.Should().NotContain(n => n.EndsWith(".sh"));
    }

    [TestMethod]
    public async Task BuildAsync_SKILL_md_has_frontmatter_and_tool_name()
    {
        var roster = McpAssistantToolNaming.AssignToolNames(
        [
            (PubId, "Architecture Guide", "Design reviews", true)
        ]);

        var service = new ClaudeSkillPackService();
        var result = await service.BuildAsync(new ClaudeSkillPackBuildRequest(
            PubId,
            "Architecture Guide",
            null,
            null,
            ApiBase,
            roster));

        var skillMd = ReadZipEntry(result.ZipBytes, "architecture-guide/SKILL.md");

        skillMd.Should().Contain("---");
        skillMd.Should().Contain("name: architecture-guide");
        skillMd.Should().Contain("allowed-tools: Bash(python:*), Bash(python3:*), Read, Write");
        skillMd.Should().Contain("Architecture_Guide");
    }

    [TestMethod]
    public async Task BuildAsync_Never_contains_real_api_key()
    {
        var roster = McpAssistantToolNaming.AssignToolNames(
        [
            (PubId, "Test Guide", "Test", true)
        ]);

        var service = new ClaudeSkillPackService();
        var result = await service.BuildAsync(new ClaudeSkillPackBuildRequest(
            PubId,
            "Test Guide",
            "test-guide",
            "Test description",
            ApiBase,
            roster));

        var env = ReadZipEntry(result.ZipBytes, "test-guide/.env");
        env.Should().Contain("GUIDEANTS_API_KEY=gak_REPLACE_ME");
        env.Should().NotMatchRegex(@"GUIDEANTS_API_KEY=gak_(?!REPLACE_ME)[A-Za-z0-9_-]+");
    }

    [TestMethod]
    public async Task BuildAsync_Env_contains_pub_id_and_api_base()
    {
        var roster = McpAssistantToolNaming.AssignToolNames(
        [
            (PubId, "Test Guide", "Test", true)
        ]);

        var service = new ClaudeSkillPackService();
        var result = await service.BuildAsync(new ClaudeSkillPackBuildRequest(
            PubId,
            "Test Guide",
            "test-guide",
            null,
            ApiBase,
            roster));

        var env = ReadZipEntry(result.ZipBytes, "test-guide/.env");
        env.Should().Contain($"GUIDEANTS_PUB_ID={PubId}");
        env.Should().Contain($"GUIDEANTS_API_BASE={ApiBase}");
    }

    [TestMethod]
    public async Task BuildAsync_Reference_lists_tool_names()
    {
        var roster = McpAssistantToolNaming.AssignToolNames(
        [
            (PubId, "Creative Guide", "Guide desc", true),
            (Guid.NewGuid(), "Researcher", "Researches", false)
        ]);

        var service = new ClaudeSkillPackService();
        var result = await service.BuildAsync(new ClaudeSkillPackBuildRequest(
            PubId,
            "Creative Guide",
            "creative-guide",
            "MCP desc",
            ApiBase,
            roster));

        var reference = ReadZipEntry(result.ZipBytes, "creative-guide/reference.md");
        reference.Should().Contain("Creative_Guide");
        reference.Should().Contain("Researcher");
    }

    [TestMethod]
    public async Task BuildAsync_SKILL_md_documents_python_client_and_file_contract()
    {
        var roster = McpAssistantToolNaming.AssignToolNames(
        [
            (PubId, "Architecture Guide", "Design reviews", true)
        ]);

        var service = new ClaudeSkillPackService();
        var result = await service.BuildAsync(new ClaudeSkillPackBuildRequest(
            PubId,
            "Architecture Guide",
            "architecture-guide",
            null,
            ApiBase,
            roster));

        var skillMd = ReadZipEntry(result.ZipBytes, "architecture-guide/SKILL.md");

        skillMd.Should().Contain("scripts/guideants_mcp.py");
        skillMd.Should().Contain("--save-dir");
        skillMd.Should().Contain("deliverables");

        // The workflow must not instruct the agent to run bundled bash scripts.
        skillMd.Should().NotContain("scripts/list-tools.sh");
        skillMd.Should().NotContain("scripts/invoke-assistant.sh");
    }

    [TestMethod]
    public async Task BuildAsync_Ships_python_client_without_bash_or_curl()
    {
        var roster = McpAssistantToolNaming.AssignToolNames(
        [
            (PubId, "Architecture Guide", "Design reviews", true)
        ]);

        var service = new ClaudeSkillPackService();
        var result = await service.BuildAsync(new ClaudeSkillPackBuildRequest(
            PubId,
            "Architecture Guide",
            "architecture-guide",
            null,
            ApiBase,
            roster));

        var client = ReadZipEntry(result.ZipBytes, "architecture-guide/scripts/guideants_mcp.py");

        client.Should().Contain("def cmd_invoke");
        client.Should().Contain("urllib.request");
        client.Should().Contain("recover");
    }

    [TestMethod]
    public void BuildSkillDescription_Falls_back_with_guide_name_and_trigger()
    {
        var description = ClaudeSkillPackService.BuildSkillDescription(null, "Architecture Guide", "architecture-guide");

        description.Should().Contain("Architecture Guide");
        description.Should().Contain("/architecture-guide");
    }

    [TestMethod]
    public void BuildSkillDescription_Prefers_author_supplied_text()
    {
        var description = ClaudeSkillPackService.BuildSkillDescription(
            "  Custom MCP description.  ", "Architecture Guide", "architecture-guide");

        description.Should().Be("Custom MCP description.");
    }

    private static string ReadZipEntry(byte[] zipBytes, string entryPath)
    {
        using var zipStream = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entry = archive.GetEntry(entryPath.Replace('/', Path.DirectorySeparatorChar))
                    ?? archive.Entries.FirstOrDefault(e =>
                        e.FullName.Replace('\\', '/').Equals(entryPath, StringComparison.OrdinalIgnoreCase));

        entry.Should().NotBeNull($"entry {entryPath} should exist");
        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
