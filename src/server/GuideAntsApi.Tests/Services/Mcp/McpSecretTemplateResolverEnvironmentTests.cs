using FluentAssertions;
using GuideAntsApi.Models.Guides;
using GuideAntsApi.Services.Mcp;

namespace GuideAntsApi.Tests.Services.Mcp;

[TestClass]
public sealed class McpSecretTemplateResolverEnvironmentTests
{
    [TestMethod]
    public void ResolveEnvironmentVariables_Resolves_secret_refs_at_call_time()
    {
        var refs = new List<McpEnvironmentVariableRefDto>
        {
            new("EXAMPLE_API_KEY", "{{secret:EXAMPLE_API_KEY}}"),
        };

        var resolved = McpSecretTemplateResolver.ResolveEnvironmentVariables(
            refs,
            new Dictionary<string, string> { ["EXAMPLE_API_KEY"] = "resolved-secret" });

        resolved.Should().ContainKey("EXAMPLE_API_KEY")
            .WhoseValue.Should().Be("resolved-secret");
    }

    [TestMethod]
    public void ResolveEnvironmentVariables_Throws_when_secret_missing()
    {
        var refs = new List<McpEnvironmentVariableRefDto>
        {
            new("MISSING", "{{secret:MISSING}}"),
        };

        Action act = () => McpSecretTemplateResolver.ResolveEnvironmentVariables(
            refs,
            new Dictionary<string, string>());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MISSING*");
    }
}
