using AntRunner.Chat;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuideAntsApi.Tests.ChatLayer;

[TestClass]
public sealed class ChatDiagnosticsTests
{
    [TestMethod]
    public void CreateLogger_Returns_logger_for_type_after_initialize()
    {
        ChatDiagnostics.Initialize(NullLoggerFactory.Instance);

        var logger = ChatDiagnostics.CreateLogger<ChatDiagnosticsTests>();

        logger.Should().NotBeNull();
        logger.Should().BeAssignableTo<ILogger>();
    }

    [TestMethod]
    public void CreateLogger_Returns_logger_for_category_name()
    {
        ChatDiagnostics.Initialize(NullLoggerFactory.Instance);

        var logger = ChatDiagnostics.CreateLogger("AntRunner.Chat.TestCategory");

        logger.Should().NotBeNull();
    }

    [TestMethod]
    public void Initialize_With_null_uses_null_logger_factory()
    {
        ChatDiagnostics.Initialize(null);

        var logger = ChatDiagnostics.CreateLogger<string>();

        logger.Should().NotBeNull();
    }
}
