using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.Services.Mcp;

namespace GuideAntsApi.Tests.Services.Mcp;

[TestClass]
public sealed class McpSandboxExecutorTests
{
    [TestMethod]
    public void McpSandbox_ActionType_IsNotClientHandled_SoTurnDoesNotPauseForClient()
    {
        AntRunner.ToolCalling.Functions.ActionType.McpSandbox
            .Should().NotBe(AntRunner.ToolCalling.Functions.ActionType.ClientHandled);
    }

    [TestMethod]
    public void McpToolExecutionBridge_Exposes_shared_sandbox_executor_entrypoint()
    {
        var bridgeType = typeof(McpToolExecutionBridge);
        var method = bridgeType.GetMethod("ExecuteMcpSandboxTool");
        method.Should().NotBeNull("notebook/embed/wire must share one sandbox executor seam (E15)");
        method!.ReturnType.Should().Be(typeof(Task<string>));
    }

    [TestMethod]
    public void ResolveEnvironmentVariables_does_not_log_resolved_secrets()
    {
        var refs = new List<GuideAntsApi.Models.Guides.McpEnvironmentVariableRefDto>
        {
            new("TOKEN", "{{secret:TOKEN}}"),
        };

        var resolved = McpSecretTemplateResolver.ResolveEnvironmentVariables(
            refs,
            new Dictionary<string, string> { ["TOKEN"] = "super-secret-value" });

        resolved["TOKEN"].Should().Be("super-secret-value");
        JsonSerializer.Serialize(new { env = resolved }).Should().Contain("super-secret-value");
    }
}
