using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.DataModel.Models;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models.Conversations;
using GuideAntsApi.Services.Conversations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using DataModelChatRole = GuideAntsApi.DataModel.Models.ChatRole;

namespace GuideAntsApi.IntegrationTests.Services.Conversations;

/// <summary>
/// Regression coverage for published model resolution. A published guide assistant that declares
/// no model of its own must resolve through <c>IChatModelResolver</c> (honoring the global
/// <c>ChatDefaults:OverrideAllChatModels</c> / <c>DefaultModelId</c> settings) rather than being
/// rejected by the service. This locks the behavior that was broken when an
/// <c>InvalidOperationException("…does not specify a model")</c> guard was added ahead of the resolver,
/// which both broke model-less bootstrap assistants and ignored the global override setting.
/// </summary>
[TestClass]
public sealed class PublishedConversationModelResolutionTests : BaseEndpointTest
{
    private const string DefaultModelId = "gpt-4.1";
    private const string FakeAssistantText = "Test assistant response.";

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext context)
    {
        SharedFactory = new GlobalOverrideChatModelFactory();
        await SharedFactory.InitializeAsync();
    }

    [ClassCleanup]
    public static Task ClassCleanup() => DisposeSharedFactoryAsync();

    [TestInitialize]
    public override async Task BaseTestInitialize()
    {
        await base.BaseTestInitialize();
        FakeChatCompletionBehavior.Instance.Reset();
        SetupAuthentication();
    }

    protected override async Task CleanDatabaseAsync()
    {
        if (SharedFactory != null)
        {
            using var scope = SharedFactory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM PublishedGuides;");
        }

        await base.CleanDatabaseAsync();
    }

    [TestMethod]
    public async Task SendMessageStream_Resolves_via_global_override_when_assistant_has_no_model()
    {
        Guid conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            conversationId = await SeedModellessGuideConversationAsync(db);
        }

        List<StreamingEvent> events;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IPublishedConversationService>();
            // No ModelDeploymentId on the request and no model on the assistant: resolution must fall
            // through to the global override default rather than throwing "does not specify a model".
            var request = new SendMessageRequest { Instructions = "Hello" };
            events = await CollectAsync(svc.SendMessageStreamAsync(
                conversationId, request, publisherId: null, externalUserIdentity: null));
        }

        events.Select(e => e.EventType).Should().Contain(StreamingEventTypes.Complete);

        using var verifyScope = SharedFactory!.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var assistantMessage = verifyDb.NotebookConversationMessages
            .Where(m => m.NotebookConversationId == conversationId && m.Role == DataModelChatRole.Assistant)
            .ToList();
        assistantMessage.Should().Contain(m => m.Content == FakeAssistantText);
    }

    private static async Task<Guid> SeedModellessGuideConversationAsync(ApplicationDbContext db)
    {
        // A guide assistant with no ModelId. DatabaseStorage projects the manifest model from
        // Assistant.ModelId (no implicit default), so AssistantUtility returns a definition whose
        // Model is null - the exact production condition for bootstrap assistants like "Media Creator".
        var guide = new Assistant
        {
            Id = Guid.NewGuid(),
            Name = $"Modelless Pilot {Guid.NewGuid():N}",
            Kind = AssistantKind.Guide,
            IsActive = true,
            IsGlobal = true,
            ModelId = null,
            Instructions = "You are a model-less pilot."
        };
        db.Assistants.Add(guide);

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Title = $"Modelless Project {Guid.NewGuid():N}",
            Slug = $"modelless-{Guid.NewGuid():N}",
            Description = "integration",
            Created = DateTime.UtcNow
        };
        db.Projects.Add(project);

        var notebook = new Notebook
        {
            Id = Guid.NewGuid(),
            Title = $"Modelless Notebook {Guid.NewGuid():N}",
            Slug = $"modellessnb-{Guid.NewGuid():N}",
            ProjectId = project.Id,
            GuideId = guide.Id,
            Created = DateTime.UtcNow
        };
        db.Notebooks.Add(notebook);

        var conversation = new NotebookConversation
        {
            NotebookId = notebook.Id,
            Title = "Model-less streaming"
        };
        db.NotebookConversations.Add(conversation);
        await db.SaveChangesAsync();

        return conversation.Id;
    }

    private static async Task<List<StreamingEvent>> CollectAsync(
        IAsyncEnumerable<StreamingEvent> stream,
        int maxSeconds = 60)
    {
        var events = new List<StreamingEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(maxSeconds));
        await foreach (var ev in stream.WithCancellation(cts.Token))
        {
            events.Add(ev);
        }
        return events;
    }

    /// <summary>
    /// Standard integration host with the global chat-model override enabled, mirroring a deployment
    /// where an operator has set "override all chat models" with a configured default.
    /// </summary>
    private sealed class GlobalOverrideChatModelFactory : TestWebApplicationFactory
    {
        protected override void ConfigureTestAppConfiguration(IConfigurationBuilder config, WebHostBuilderContext context)
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ChatDefaults:OverrideAllChatModels"] = "true",
                ["ChatDefaults:DefaultModelId"] = DefaultModelId
            });
        }
    }
}
