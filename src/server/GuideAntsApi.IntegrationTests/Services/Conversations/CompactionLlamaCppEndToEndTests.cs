using System.Net;
using System.Net.Http.Json;
using AntRunner.Chat.Abstractions;
using FluentAssertions;
using GuideAntsApi.DataModel;
using GuideAntsApi.IntegrationTests.Infrastructure;
using GuideAntsApi.Models.Conversations;
using Microsoft.Extensions.DependencyInjection;

namespace GuideAntsApi.IntegrationTests.Services.Conversations;

/// <summary>
/// Spec "Integration": the compaction story against a real small-context llama.cpp. Opt-in -- set
/// GA_COMPACTION_E2E_LLAMA_URL (see docs/compaction-llamacpp-e2e-runbook.md). Without it every test here
/// is Inconclusive, never Passed. Asserts that recall is offered, not that a small model chooses to call
/// it; CompactionEndToEndTests proves recall executes.
/// </summary>
[TestClass]
[TestCategory("LocalLlamaE2E")]
public sealed class CompactionLlamaCppEndToEndTests : BaseEndpointTest
{
    private const string BaseUrlEnv = "GA_COMPACTION_E2E_LLAMA_URL";
    private const string ModelEnv = "GA_COMPACTION_E2E_LLAMA_MODEL";
    private const int MaxTurnsBeforeOverflow = 40;
    private const int PaddingChars = 1500;

    private static LlamaCompactionWebApplicationFactory? _llamaFactory;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext context)
    {
        var baseUrl = Environment.GetEnvironmentVariable(BaseUrlEnv);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return;
        }

        _llamaFactory = new LlamaCompactionWebApplicationFactory(
            baseUrl, Environment.GetEnvironmentVariable(ModelEnv) ?? "local");
        SharedFactory = _llamaFactory;
        await SharedFactory.InitializeAsync();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        await DisposeSharedFactoryAsync();
        _llamaFactory = null;
    }

    [TestInitialize]
    public override async Task BaseTestInitialize()
    {
        if (_llamaFactory == null)
        {
            Assert.Inconclusive(
                $"Set {BaseUrlEnv} (and optionally {ModelEnv}) to a llama-server started per " +
                "docs/compaction-llamacpp-e2e-runbook.md to run this test.");
        }

        await base.BaseTestInitialize();
    }

    [TestMethod]
    public async Task RealLlamaCpp_Overflows_Compacts_Continues_AndOffersRecall()
    {
        Guid projectId, notebookId, conversationId;
        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (projectId, notebookId) = await ConversationStreamTestHelpers.SeedProjectNotebookAsync(db, "Llama E2E");
            conversationId = await ConversationStreamTestHelpers.SeedConversationAsync(db, notebookId, "Llama E2E");
        }

        Task<List<(string EventType, string Payload)>> Send(string text) =>
            ConversationStreamTestHelpers.SendMessageStreamAsync(
                Client, projectId, notebookId, conversationId, new { instructions = text, assistantName = "assistant" });

        // Grow the conversation until the real server rejects it.
        Dictionary<Guid, string>? beforeOverflow = null;
        var overflowed = false;
        for (var turn = 1; turn <= MaxTurnsBeforeOverflow && !overflowed; turn++)
        {
            using (var scope = SharedFactory!.Services.CreateScope())
            {
                beforeOverflow = await ConversationStreamTestHelpers.SnapshotMessageContentAsync(
                    scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), conversationId);
            }

            var events = await Send($"Note {turn}: " + new string('x', PaddingChars) + " Reply with the single word OK.");
            var error = events.FirstOrDefault(e => e.EventType == StreamingEventTypes.Error);
            if (error != default)
            {
                error.Payload.Should().Contain("chat_context_overflow",
                    "the only acceptable failure while growing the conversation is a classified overflow");
                overflowed = true;
            }
        }

        overflowed.Should().BeTrue(
            $"a 2048-token window must overflow within {MaxTurnsBeforeOverflow} padded turns; if not, the " +
            "server is running with a larger -c or with context shift enabled (see the runbook)");

        using (var scope = SharedFactory!.Services.CreateScope())
        {
            var after = await ConversationStreamTestHelpers.SnapshotMessageContentAsync(
                scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(), conversationId);
            foreach (var (id, content) in beforeOverflow!)
            {
                after[id].Should().Be(content, "D5: overflow must never rewrite a stored message");
            }
        }

        var compactResponse = await Client.PostAsync(
            $"/api/projects/{projectId}/notebooks/{notebookId}/conversations/{conversationId}/compact", content: null);
        compactResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await compactResponse.Content.ReadFromJsonAsync<CompactionResultDto>())!
            .BoundaryTurnIndex.Should().NotBeNull();

        var continued = await Send("Reply with the single word OK.");
        continued.Should().NotContain(e => e.EventType == StreamingEventTypes.Error,
            "after compaction the same model must accept the next turn");
        continued.Should().Contain(e => e.EventType == StreamingEventTypes.Complete);

        var recorder = _llamaFactory!.Recorder;
        string.Join("\n", recorder.LastRequestMessages!.Select(m => m.GetText()))
            .Should().Contain(ConversationStreamTestHelpers.HandoffFramingFragment);
        recorder.LastRequestToolNames.Should().Contain("conversation_recall");
    }
}
