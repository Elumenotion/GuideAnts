using AntRunner.ToolCalling;
using FluentAssertions;

namespace GuideAntsApi.Tests.ToolCalling;

[TestClass]
public sealed class ToolOperationIdSanitizerTests
{
    [TestMethod]
    public void ToWireName_ReplacesInvalidCharacters()
    {
        ToolOperationIdSanitizer.ToWireName("skills.list").Should().Be("skills_list");
        ToolOperationIdSanitizer.ToWireName("search/files").Should().Be("search_files");
    }

    [TestMethod]
    public void ToWireName_PreservesValidNames()
    {
        ToolOperationIdSanitizer.ToWireName("SearchAssistantFiles").Should().Be("SearchAssistantFiles");
        ToolOperationIdSanitizer.ToWireName("run_python").Should().Be("run_python");
        ToolOperationIdSanitizer.ToWireName("skills_list").Should().Be("skills_list");
    }

    [TestMethod]
    public void ToWireName_EnforcesMaxLength()
    {
        var longName = new string('a', 80);
        ToolOperationIdSanitizer.ToWireName(longName).Should().HaveLength(64);
        ToolOperationIdSanitizer.IsWireCompatible(ToolOperationIdSanitizer.ToWireName(longName)).Should().BeTrue();
    }

    [TestMethod]
    public void ToUniqueWireName_ResolvesCollisions()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ToolOperationIdSanitizer.ToUniqueWireName("my.tool", used).Should().Be("my_tool");
        ToolOperationIdSanitizer.ToUniqueWireName("my@tool", used).Should().Be("my_tool_2");
    }
}
