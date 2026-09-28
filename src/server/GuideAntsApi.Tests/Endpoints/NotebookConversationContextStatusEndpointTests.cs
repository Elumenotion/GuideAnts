using System.Text.Json;
using FluentAssertions;
using GuideAntsApi.Endpoints;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Conversations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json.Serialization;

namespace GuideAntsApi.Tests.Endpoints;

[TestClass]
public sealed class NotebookConversationContextStatusEndpointTests
{
    [TestMethod]
    public async Task GetConversation_IncludesContextStatus()
    {
        var id = Guid.NewGuid();
        var svc = ConversationService(id);
        var status = new Mock<IConversationContextStatusService>();
        status.Setup(s => s.GetAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationContextStatusDto(200_000, 12_345, null, ContextEstimateSource.ProviderUsage, "m1", GuideAntsApi.Services.Routing.ContextWindowSource.Catalog));

        var result = await NotebookConversationsEndpoints.GetConversationAsync(
            svc.Object, status.Object, NullLogger.Instance, id, CancellationToken.None);
        var (code, body) = await ExecuteAsync(result);

        code.Should().Be(StatusCodes.Status200OK);
        using var doc = JsonDocument.Parse(body);
        var cs = doc.RootElement.GetProperty("contextStatus");
        cs.GetProperty("contextWindowTokens").GetInt32().Should().Be(200000);
        cs.GetProperty("estimatedPromptTokens").GetInt32().Should().Be(12345);
        cs.GetProperty("estimateSource").GetString().Should().Be("ProviderUsage");
    }

    [TestMethod]
    public async Task GetConversation_StatusFailure_StillReturnsTheConversation()
    {
        var id = Guid.NewGuid();
        var svc = ConversationService(id);
        var status = new Mock<IConversationContextStatusService>();
        status.Setup(s => s.GetAsync(id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var result = await NotebookConversationsEndpoints.GetConversationAsync(
            svc.Object, status.Object, NullLogger.Instance, id, CancellationToken.None);
        var (code, body) = await ExecuteAsync(result);

        code.Should().Be(StatusCodes.Status200OK);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("title").GetString().Should().Be("T");
        doc.RootElement.GetProperty("contextStatus").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private static Mock<IConversationService> ConversationService(Guid id)
    {
        var svc = new Mock<IConversationService>();
        svc.Setup(s => s.GetConversationWithMessagesAsync(id))
            .ReturnsAsync(new NotebookConversationWithMessagesDto(
                id, "T", null, DateTime.UtcNow, null, new List<MessageDto>()));
        return svc;
    }

    private static async Task<(int Code, string Body)> ExecuteAsync(IResult result)
    {
        var ctx = new DefaultHttpContext();
        ctx.RequestServices = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .BuildServiceProvider();
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync());
    }
}
