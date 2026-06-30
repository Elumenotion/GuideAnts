using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ScriptExecutionAgent.Tests.Infrastructure;

namespace ScriptExecutionAgent.Tests.InProcess;

[TestClass]
public sealed class ScriptExecutionScopeRuntimeTests
{
    private const string ProbeVariable = "GA_MCP_ENV_ISOLATION_PROBE";

    [TestCleanup]
    public void TearDown() => Environment.SetEnvironmentVariable(ProbeVariable, null);

    [TestMethod]
    public void BuildScriptEnvironment_uses_shared_project_runtime_root_for_tool_caches()
    {
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var guideId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var scope = ScriptExecutionScopeRuntime.ResolveScope(
            projectId,
            guideId,
            new ScriptExecutionScopeOptions("/tmp/scopes", null, null, false, null));

        var workingDirectory = "/app/ContentFiles/project/notebooks/nb/Output";
        var environment = ScriptExecutionScopeRuntime.BuildScriptEnvironment(
            scope,
            null,
            workingDirectory,
            NullLogger.Instance);

        var expectedRuntimeRoot = Path.Combine(
            "/tmp/scopes",
            $"project-{projectId:N}",
            "runtime");

        environment["HOME"].Should().Be(expectedRuntimeRoot);
        environment["HOME"].Should().NotBe(workingDirectory);
        environment["XDG_CACHE_HOME"].Should().Be(Path.Combine(expectedRuntimeRoot, "cache"));
        environment["XDG_CONFIG_HOME"].Should().Be(Path.Combine(expectedRuntimeRoot, "config"));
    }

    [TestMethod]
    public void BuildScriptEnvironment_shares_project_runtime_root_across_guides()
    {
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var guideA = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var guideB = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var options = new ScriptExecutionScopeOptions("/tmp/scopes", null, null, false, null);

        var scopeA = ScriptExecutionScopeRuntime.ResolveScope(projectId, guideA, options);
        var scopeB = ScriptExecutionScopeRuntime.ResolveScope(projectId, guideB, options);

        scopeA.ProjectRuntimeRootPath.Should().Be(scopeB.ProjectRuntimeRootPath);
        scopeA.ProjectRuntimeRootPath.Should().Be(
            Path.Combine("/tmp/scopes", $"project-{projectId:N}", "runtime"));
    }

    [TestMethod]
    public void BuildIsolatedProcessLaunchPlan_on_linux_uses_env_i_with_curated_variables_only()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var scopedEnvironment = new Dictionary<string, string?>
        {
            ["HOME"] = "/tmp/runtime",
            ["PATH"] = "/usr/bin",
        };

        var launch = ScriptExecutionScopeRuntime.BuildIsolatedProcessLaunchPlan(
            "npx",
            ["--yes", "@example/mcp"],
            scopedEnvironment);

        if (File.Exists("/usr/bin/env"))
        {
            launch.Command.Should().Be("/usr/bin/env");
            launch.Arguments[0].Should().Be("-i");
            launch.Arguments.Should().Contain("HOME=/tmp/runtime");
            launch.Arguments.Should().Contain("PATH=/usr/bin");
            launch.Arguments.Should().EndWith("@example/mcp");
            launch.ProcessEnvironment.Should().BeNull();
        }
    }

    [TestMethod]
    public void BuildTransportEnvironment_includes_only_scoped_non_null_values()
    {
        Environment.SetEnvironmentVariable(ProbeVariable, "container-secret");
        Environment.SetEnvironmentVariable("SCRIPT_EXECUTION_AGENT_TOKEN", "agent-token");

        var scopedEnvironment = new Dictionary<string, string?>
        {
            ["HOME"] = "/tmp/runtime",
            ["PATH"] = "/usr/bin",
            ["EMPTY"] = null,
        };

        var transport = ScriptExecutionScopeRuntime.BuildTransportEnvironment(scopedEnvironment);

        transport.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["HOME"] = "/tmp/runtime",
            ["PATH"] = "/usr/bin",
        });
        transport.Should().NotContainKey(ProbeVariable);
        transport.Should().NotContainKey("SCRIPT_EXECUTION_AGENT_TOKEN");
        transport.Should().NotContainKey("EMPTY");
    }
}
