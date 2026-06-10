using AntRunner.ToolCalling;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.ToolCalling;

[TestClass]
public sealed class ToolCallingDiagnosticsTests
{
    [TestMethod]
    public void CreateLogger_ReturnsLoggerForGenericType()
    {
        ToolCallingDiagnostics.Initialize(NullLoggerFactory.Instance);

        var logger = ToolCallingDiagnostics.CreateLogger<ToolCallingDiagnosticsTests>();

        logger.Should().NotBeNull();
        logger.Should().BeAssignableTo<ILogger>();
    }

    [TestMethod]
    public void CreateLogger_ReturnsLoggerForCategory()
    {
        ToolCallingDiagnostics.Initialize(NullLoggerFactory.Instance);

        var logger = ToolCallingDiagnostics.CreateLogger("AntRunner.ToolCalling.Tests");

        logger.Should().NotBeNull();
    }

    [TestMethod]
    public void Initialize_WithNullFactory_FallsBackToNullLoggerFactory()
    {
        ToolCallingDiagnostics.Initialize(null);

        var logger = ToolCallingDiagnostics.CreateLogger<string>();

        logger.Should().NotBeNull();
    }
}
