using System.Text.Json;
using AntRunner.ToolCalling;
using AntRunner.ToolCalling.Functions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.Services;
using GuideAntsApi.Services.Conversations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.Tests.Services;

[TestClass]
[DoNotParallelize] // ConversationRecallTools holds a process-global service provider
public sealed class ConversationRecallToolsTests
{
    private const string MethodName = "GuideAntsApi.Services.ConversationRecallTools.RecallConversation";

    private static ServiceProvider BuildProvider(string dbName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IConversationRecallService, ConversationRecallService>();
        return services.BuildServiceProvider();
    }

    private static async Task<Guid> SeedCompactedConversationAsync(ServiceProvider provider)
    {
        var conversationId = Guid.NewGuid();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.NotebookConversations.Add(new NotebookConversation
        {
            Id = conversationId,
            NotebookId = Guid.NewGuid(),
            Title = "t",
            CompactionBoundaryTurnIndex = 3
        });
        db.NotebookConversationMessages.Add(new NotebookConversationMessage
        {
            NotebookConversationId = conversationId,
            TurnIndex = 1,
            MessageSequence = 0,
            Role = DataModelChatRole.User,
            Content = "please remember the widget calibration constant is 7"
        });
        await db.SaveChangesAsync();
        return conversationId;
    }

    [TestMethod]
    public async Task RecallConversation_ScopesTheSearchToTheContextsConversation()
    {
        var provider = BuildProvider(nameof(RecallConversation_ScopesTheSearchToTheContextsConversation));
        ConversationRecallTools.InitializeServiceProvider(provider);
        var conversationId = await SeedCompactedConversationAsync(provider);

        var context = new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), conversationId);

        var json = await ConversationRecallTools.RecallConversation("widget calibration", 1, context);

        json.Should().Contain("widget calibration constant is 7");
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("totalMatches").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("boundaryTurnIndex").GetInt32().Should().Be(3);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public async Task RecallConversation_BlankQuery_ReturnsAJsonErrorRatherThanAnything(string query)
    {
        var provider = BuildProvider($"{nameof(RecallConversation_BlankQuery_ReturnsAJsonErrorRatherThanAnything)}-{query.Length}");
        ConversationRecallTools.InitializeServiceProvider(provider);
        var conversationId = await SeedCompactedConversationAsync(provider);

        var json = await ConversationRecallTools.RecallConversation(
            query, 1, new InvocationContext(Guid.NewGuid(), Guid.NewGuid(), conversationId));

        using var document = JsonDocument.Parse(json);
        document.RootElement.TryGetProperty("error", out _).Should().BeTrue();
        json.Should().NotContain("widget calibration constant");
    }

    [TestMethod]
    public async Task RecallConversation_WithoutContext_ReturnsAJsonError()
    {
        var provider = BuildProvider(nameof(RecallConversation_WithoutContext_ReturnsAJsonError));
        ConversationRecallTools.InitializeServiceProvider(provider);

        var json = await ConversationRecallTools.RecallConversation("widget", 1, context: null);

        using var document = JsonDocument.Parse(json);
        document.RootElement.TryGetProperty("error", out _).Should().BeTrue();
    }

    [TestMethod]
    public void GeneratedSchema_ExposesOnlyQueryAndPage_AndNeverAConversationId()
    {
        ToolContractRegistry.RefreshContracts();

        var schemaJson = ToolContractRegistry.GenerateOpenApiSchema(MethodName);

        using var document = JsonDocument.Parse(schemaJson);
        var properties = document.RootElement
            .GetProperty("paths").GetProperty(MethodName).GetProperty("post")
            .GetProperty("requestBody").GetProperty("content").GetProperty("application_json")
            .GetProperty("schema").GetProperty("properties");

        properties.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(new[] { "query", "page" },
                "the model may only supply the query and the page; scoping is structural");

        schemaJson.Should().NotContain("conversationId");
        schemaJson.Should().NotContain("context");
    }

    [TestMethod]
    public void ToolContract_IsRegisteredAsRequiringNotebookContext()
    {
        ToolContractRegistry.RefreshContracts();

        var contract = ToolContractRegistry.GetContract(MethodName);

        contract.ToolMetadata.Should().NotBeNull();
        contract.ToolMetadata!.OperationId.Should().Be("conversation_recall");
        contract.RequiresNotebookContext.Should().BeTrue(
            "without this attribute ThreadRun never injects InvocationContext and the tool cannot resolve a conversation");
    }

    [TestMethod]
    public void ToolCall_SupplyingAnExtraConversationId_FailsValidationInsteadOfReadingAnotherConversation()
    {
        ToolContractRegistry.RefreshContracts();
        var schemaJson = ToolContractRegistry.GenerateOpenApiSchema(MethodName);
        var validation = OpenApiHelper.ValidateAndParseOpenApiSpec(schemaJson);
        validation.Status.Should().BeTrue();

        // The single-argument overload is deliberate: the assistant-name overload is async and
        // reads DomainAuth from the database, which this unit test has no reason to stand up.
        var builders = ToolCaller.GetToolCallers(validation.Spec!);
        var builder = builders["conversation_recall"].Clone();
        builder.Params = new Dictionary<string, object>
        {
            ["query"] = "widget",
            ["conversationId"] = Guid.NewGuid().ToString()
        };

        var (isValid, errorMessage) = builder.ValidateParamsAgainstSchema();

        isValid.Should().BeFalse("an invented conversation id must be rejected, not silently honored");
        errorMessage.Should().Contain("conversationId");
    }
}
