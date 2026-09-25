using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.Endpoints;
using GuideAntsApi.Services.Conversations.Commands;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GuideAntsApi.Tests.Endpoints;

[TestClass]
public sealed class NotebookConversationCompactEndpointTests
{
    [TestMethod]
    public async Task Compact_ReturnsBoundaryAndEstimates()
    {
        var convoId = Guid.NewGuid();
        var service = new Mock<ICompactionService>();
        service.Setup(s => s.CompactConversationAsync(convoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompactionOutcome(4, 12, 5000, 900));

        var result = await NotebookConversationsEndpoints.CompactConversationAsync(
            service.Object, NullLogger.Instance, convoId, CancellationToken.None);
        var (code, body) = await ExecuteAsync(result);

        code.Should().Be(StatusCodes.Status200OK);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("boundaryTurnIndex").GetInt32().Should().Be(4);
        doc.RootElement.GetProperty("messagesSummarized").GetInt32().Should().Be(12);
        doc.RootElement.GetProperty("estimatedTokensBefore").GetInt32().Should().Be(5000);
        doc.RootElement.GetProperty("estimatedTokensAfter").GetInt32().Should().Be(900);
    }

    [TestMethod]
    public async Task Compact_ConversationNotFound_Returns404()
    {
        var convoId = Guid.NewGuid();
        var service = new Mock<ICompactionService>();
        service.Setup(s => s.CompactConversationAsync(convoId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("Conversation not found"));

        var result = await NotebookConversationsEndpoints.CompactConversationAsync(
            service.Object, NullLogger.Instance, convoId, CancellationToken.None);
        var (code, _) = await ExecuteAsync(result);

        code.Should().Be(StatusCodes.Status404NotFound);
    }

    [TestMethod]
    public async Task Compact_ConversationLocked_Returns409NamingTheHolder()
    {
        var convoId = Guid.NewGuid();
        var service = new Mock<ICompactionService>();
        service.Setup(s => s.CompactConversationAsync(convoId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Conversation is locked by someone-else"));

        var result = await NotebookConversationsEndpoints.CompactConversationAsync(
            service.Object, NullLogger.Instance, convoId, CancellationToken.None);
        var (code, body) = await ExecuteAsync(result);

        code.Should().Be(StatusCodes.Status409Conflict);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("Conversation is locked by");
    }

    private static async Task<(int Code, string Body)> ExecuteAsync(IResult result)
    {
        var ctx = new DefaultHttpContext();
        ctx.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync());
    }
}
