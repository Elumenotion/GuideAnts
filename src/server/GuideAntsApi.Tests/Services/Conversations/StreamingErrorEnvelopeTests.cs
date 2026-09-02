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
  public void Build_Maps_llama_inference_timeout_to_recovery_code()
  {
    var inner = new LlamaInferenceTimeoutException("qwen3.5-27b", 300);
    var ex = new ChatConversationException(inner, chatRunOutput: null);

    var json = Serialize(StreamingErrorEnvelope.Build(ex));

    json.GetProperty("code").GetString().Should().Be("local_llm_timeout");
    json.GetProperty("routerModelId").GetString().Should().Be("qwen3.5-27b");
    json.GetProperty("timeoutSeconds").GetInt32().Should().Be(300);
  }

  [TestMethod]
  public void Build_Leaves_vision_capability_rejection_without_crash_code()
  {
    var ex = new InvalidOperationException(
      "This model does not support image attachments. Remove the image from your message or enable vision (mmproj) in the model preset.");

    var json = Serialize(StreamingErrorEnvelope.Build(ex));

    json.TryGetProperty("code", out var codeEl).Should().BeTrue();
    codeEl.ValueKind.Should().Be(JsonValueKind.Null);
    json.GetProperty("type").GetString().Should().Be(nameof(InvalidOperationException));
    json.GetProperty("message").GetString().Should().Contain("does not support image attachments");
  }

  [TestMethod]
  public void Build_Maps_active_timeout_recovery_to_recovering_code()
  {
    var ex = new LlamaRuntimeCrashedException(
      LlamaRuntimeCrashReason.Recovering,
      "Runtime recovery is active.",
      statusCode: null,
      upstreamDetail: null);

    var json = Serialize(StreamingErrorEnvelope.Build(ex));

    json.GetProperty("code").GetString().Should().Be("local_llm_recovering");
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

  [TestMethod]
  public void Build_Includes_turnId_when_provided()
  {
    var turnId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    var json = Serialize(StreamingErrorEnvelope.Build(new InvalidOperationException("boom"), turnId));

    json.GetProperty("turnId").GetGuid().Should().Be(turnId);
    json.GetProperty("message").GetString().Should().Be("boom");
  }

  private static JsonElement Serialize(object envelope) =>
    JsonSerializer.SerializeToElement(envelope);
}
