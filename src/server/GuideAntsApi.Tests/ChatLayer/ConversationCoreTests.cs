using AntRunner.Chat;
using FluentAssertions;

namespace GuideAntsApi.Tests.ChatLayer;

[TestClass]
public sealed class ConversationCoreTests
{
    [TestMethod]
    public void EventArgs_Constructors_assign_expected_properties()
    {
        var message = new MessageAddedEventArgs(
            role: "assistant",
            newMessage: "hello",
            toolCallId: "tool-1",
            functionName: "search_docs",
            toolCallsJson: "{\"id\":\"tool-1\"}");
        var external = new ExternalToolCallEventArgs("{\"call\":\"x\"}");
        var progress = new StreamingMessageProgressEventArgs("assistant", "delta");

        message.Role.Should().Be("assistant");
        message.Message.Should().Be("hello");
        message.ToolCallId.Should().Be("tool-1");
        message.FunctionName.Should().Be("search_docs");
        message.ToolCallsJson.Should().Contain("tool-1");
        external.ToolCallsJson.Should().Contain("call");
        progress.Role.Should().Be("assistant");
        progress.ContentDelta.Should().Be("delta");
    }

    [TestMethod]
    public async Task Conversation_ChangeAssistant_throws_when_uninitialized_instance_is_used()
    {
        var conversation = new Conversation();

        var act = async () => await conversation.ChangeAssistant(
            "any-assistant",
            new AntRunner.Chat.Abstractions.ResolvedExecutionPolicy(
                "model",
                "provider",
                AntRunner.Chat.Abstractions.ParameterAuthority.AssistantDefinition,
                new Dictionary<string, System.Text.Json.JsonElement>()));

        await act.Should().ThrowAsync<Exception>()
            .WithMessage("*AssistantDefinition is null*");
    }

    [TestMethod]
    public void EnvironmentSettings_Set_writes_variables()
    {
        var key = $"GA_TEST_{Guid.NewGuid():N}";
        var input = new Dictionary<string, string> { [key] = "value" };

        EnvironmentSettings.Set(input);

        Environment.GetEnvironmentVariable(key).Should().Be("value");
        Environment.SetEnvironmentVariable(key, null);
    }

    [TestMethod]
    public void AntLoader_LoadAssembly_throws_for_nonexistent_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.dll");

        var act = () => AntLoader.LoadAssembly(path);

        act.Should().Throw<FileNotFoundException>();
    }
}

