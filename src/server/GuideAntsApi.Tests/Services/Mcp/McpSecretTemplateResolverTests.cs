using FluentAssertions;
using GuideAntsApi.Services.Mcp;

namespace GuideAntsApi.Tests.Services.Mcp;

[TestClass]
public sealed class McpSecretTemplateResolverTests
{
    [TestMethod]
    public void ResolveHeaders_Substitutes_secret_templates_at_call_time()
    {
        var headers = McpSecretTemplateResolver.ResolveHeaders(
            new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer {{secret:MCP_API_KEY}}",
                ["X-Custom"] = "plain-value",
            },
            new Dictionary<string, string>
            {
                ["MCP_API_KEY"] = "super-secret-token",
            });

        headers["Authorization"].Should().Be("Bearer super-secret-token");
        headers["X-Custom"].Should().Be("plain-value");
        headers.Values.Should().NotContain("super-secret-token", "resolved secret must not appear as standalone header value leak check");
    }

    [TestMethod]
    public void ResolveHeaders_Throws_when_secret_missing()
    {
        Action act = () => McpSecretTemplateResolver.ResolveHeaders(
            new Dictionary<string, string> { ["Authorization"] = "{{secret:MISSING}}" },
            new Dictionary<string, string>());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MISSING*");
    }

    [TestMethod]
    public void ResolveHeaders_Does_not_echo_redacted_preview_values()
    {
        Action act = () => McpSecretTemplateResolver.ResolveHeaders(
            new Dictionary<string, string> { ["Authorization"] = "***" },
            new Dictionary<string, string> { ["MCP_API_KEY"] = "real-secret" });

        act.Should().NotThrow();
        McpSecretTemplateResolver.ResolveHeaders(
            new Dictionary<string, string> { ["Authorization"] = "***" },
            new Dictionary<string, string>()).Should().ContainKey("Authorization")
            .WhoseValue.Should().Be("***");
    }
}
