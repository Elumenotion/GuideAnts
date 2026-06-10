using System.Net;
using System.Text.Json;
using AntRunner.Chat;
using AntRunner.Chat.LlamaCpp;
using FluentAssertions;
using GuideAntsApi.Services.Conversations;
using GuideAntsApi.Services.Routing;

namespace GuideAntsApi.Tests.Services.Conversations;

[TestClass]
public sealed class StreamingErrorEnvelopeTests
{
  [TestMethod]
  public void Build_Maps_llama_oom_to_local_llm_oom_code()
  {
    var ex = new LlamaRuntimeCrashedException(
      LlamaRuntimeCrashReason.OutOfMemory,
      "CUDA OOM",
      HttpStatusCode.InternalServerError,
      "allocator failed");

    var json = Serialize(StreamingErrorEnvelope.Build(ex));

    json.GetProperty("code").GetString().Should().Be("local_llm_oom");
    json.GetProperty("reason").GetString().Should().Be(nameof(LlamaRuntimeCrashReason.OutOfMemory));
  }

  [TestMethod]
  public void Build_Maps_routing_exception_with_blockers()
  {
    var ex = RoutingException.ProviderNotReady(
      "OpenAI",
      ["ApiKey missing"],
      serviceId: "chat",
      modeId: "openai-chat");

    var json = Serialize(StreamingErrorEnvelope.Build(ex));

    json.GetProperty("code").GetString().Should().Be(RoutingErrorCodes.ProviderNotReady);
    json.GetProperty("action").GetString().Should().Contain("Settings");
    json.GetProperty("blockers").GetArrayLength().Should().Be(1);
  }

  [TestMethod]
  public void Build_Wraps_chat_conversation_exception_inner_llama_crash()
  {
    var inner = new LlamaRuntimeCrashedException(
      LlamaRuntimeCrashReason.NotReady,
      "No model loaded",
      HttpStatusCode.BadRequest,
      null);
    var ex = new ChatConversationException(inner, chatRunOutput: null);

    var json = Serialize(StreamingErrorEnvelope.Build(ex));

    json.GetProperty("code").GetString().Should().Be("local_llm_not_ready");
  }

  [TestMethod]
  public void Build_Adds_auth_action_for_unauthorized_http_errors()
  {
    var ex = new HttpRequestException("401 Unauthorized", null, HttpStatusCode.Unauthorized);

    var json = Serialize(StreamingErrorEnvelope.Build(ex));

    json.GetProperty("statusCode").GetInt32().Should().Be(401);
    json.GetProperty("action").GetString().Should().Contain("Settings");
  }

  [TestMethod]
  public void Build_Uses_default_message_when_exception_message_blank()
  {
    var json = Serialize(StreamingErrorEnvelope.Build(new Exception("   ")));

    json.GetProperty("message").GetString().Should().Be("Chat run failed.");
  }

  private static JsonElement Serialize(object envelope) =>
    JsonSerializer.SerializeToElement(envelope);
}
