using AntRunner.Chat;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer;

/// <summary>
/// <see cref="ChatUsage"/> wires a process-wide static recorder built from the
/// <c>ConnectionStrings:DefaultConnection</c> environment variable. These tests
/// exercise the public recording surface up to the point where the underlying
/// recorder validates its inputs (which happens before any database access), so
/// they assert real behaviour without requiring a live SQL Server.
/// </summary>
[TestClass]
public sealed class ChatUsageTests
{
    [ClassInitialize]
    public static void EnsureRecorderConfigured(TestContext context)
    {
        // The static constructor of ChatUsage runs on first member access and
        // requires this variable to be present to build its recorder.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ConnectionStrings:DefaultConnection")))
        {
            Environment.SetEnvironmentVariable(
                "ConnectionStrings:DefaultConnection",
                "Server=localhost;Database=guideants_chatusage_tests;Trusted_Connection=True;TrustServerCertificate=True;");
        }
    }

    [TestMethod]
    public void RecordChatCompletion_WithBlankService_ValidatesThroughRecorder()
    {
        Action act = () => ChatUsage.RecordChatCompletion(
            projectId: Guid.NewGuid(),
            notebookId: Guid.NewGuid(),
            conversationId: Guid.NewGuid(),
            service: " ",
            operation: "chat",
            modelDeploymentId: "gpt-4o",
            inputTokens: 10,
            cachedInputTokens: 0,
            reasoningTokens: 0,
            outputTokens: 5,
            assistantId: Guid.NewGuid(),
            agentInvocationId: Guid.NewGuid());

        act.Should().Throw<ArgumentException>().WithMessage("Service is required*");
    }

    [TestMethod]
    public void RecordToolCall_WithBlankFunctionName_ValidatesThroughRecorder()
    {
        Action act = () => ChatUsage.RecordToolCall(
            projectId: Guid.NewGuid(),
            notebookId: Guid.NewGuid(),
            conversationId: Guid.NewGuid(),
            functionName: "",
            assistantId: Guid.NewGuid(),
            agentInvocationId: Guid.NewGuid());

        // functionName flows through as the recorder "operation", which is required.
        act.Should().Throw<ArgumentException>().WithMessage("Operation is required*");
    }
}
