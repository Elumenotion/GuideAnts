using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AntRunner.Chat.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AntRunner.Chat.LlamaCpp;

public sealed class LlamaCppChatClient : IChatCompletionClient
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private const string DefaultThoughtBlockPattern = @"<think>[\s\S]*?</think>";

    private readonly HttpClient _httpClient;
    private readonly LlamaCppConfig _config;
    private readonly string? _deploymentId;
    private readonly LlamaCppRuntimeProfileData? _profileData;
    private readonly bool _parallelToolCalls;
    private readonly ILogger<LlamaCppChatClient> _logger;
    private readonly IReadOnlyList<OutputStripRule> _assistantOutputStripRules;

    public LlamaCppChatClient(
        HttpClient httpClient,
        LlamaCppConfig config,
        string? deploymentId,
        LlamaCppRuntimeProfileData? profileData = null,
        bool parallelToolCalls = false,
        ILogger<LlamaCppChatClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _deploymentId = deploymentId;
        _profileData = profileData;
        _parallelToolCalls = parallelToolCalls;
        _logger = logger ?? NullLogger<LlamaCppChatClient>.Instance;
        _assistantOutputStripRules = BuildAssistantOutputStripRules();
    }

    public async Task<ChatCompletionResponse> GetCompletionAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var (jsonBody, diagnosticInfo) = BuildRequestBody(request, stream: false);
        LogRequestPayload(jsonBody, diagnosticInfo);
        var requestJson = jsonBody.ToJsonString(RequestJsonOptions);
        using var httpRequest = BuildHttpRequest(requestJson);
        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessWithDiagnosticsAsync(response, requestJson, diagnosticInfo, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        using var doc = JsonDocument.Parse(payload);
        return ParseCompletionResponse(doc.RootElement, _assistantOutputStripRules);
    }

    public async Task<ChatCompletionResponse> StreamCompletionAsync(
        ChatCompletionRequest request,
        Action<ChatCompletionChunk> onChunk,
        CancellationToken cancellationToken = default)
    {
        var (jsonBody, diagnosticInfo) = BuildRequestBody(request, stream: true);
        LogRequestPayload(jsonBody, diagnosticInfo);
        var requestJson = jsonBody.ToJsonString(RequestJsonOptions);
        using var httpRequest = BuildHttpRequest(requestJson);
        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessWithDiagnosticsAsync(response, requestJson, diagnosticInfo, cancellationToken);

        var accumulator = new StreamingAccumulator(_assistantOutputStripRules);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rawLine = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                if (!rawLine.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var payload = rawLine[5..].Trim();
                if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
                {
                    break;
                }

                using var doc = JsonDocument.Parse(payload);
                accumulator.ApplyChunk(doc.RootElement, onChunk);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsMidStreamTransportFailure(ex))
        {
            _logger.LogError(
                ex,
                "llama.cpp stream ended unexpectedly mid-response. {DiagnosticInfo}",
                diagnosticInfo);
            throw new LlamaRuntimeCrashedException(
                LlamaRuntimeCrashReason.Crashed,
                "The local model stopped responding mid-stream. The runtime likely crashed and must be restarted.",
                response.StatusCode,
                upstreamDetail: ex.Message,
                innerException: ex);
        }

        accumulator.FlushPendingAssistantText(onChunk);
        return accumulator.ToResponse();
    }

    private static bool IsMidStreamTransportFailure(Exception ex) =>
        ex is IOException or HttpRequestException;

    private IReadOnlyList<OutputStripRule> BuildAssistantOutputStripRules()
    {
        var sourcePatterns = new List<string> { DefaultThoughtBlockPattern };

        if (!string.IsNullOrWhiteSpace(_profileData?.ThoughtBlockPattern))
        {
            sourcePatterns.Add(_profileData.ThoughtBlockPattern);
        }

        if (_config.OutputStripPatterns is { Count: > 0 })
        {
            sourcePatterns.AddRange(_config.OutputStripPatterns);
        }

        var rules = new List<OutputStripRule>();
        var seenPatterns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pattern in sourcePatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            var trimmed = pattern.Trim();
            if (!seenPatterns.Add(trimmed))
            {
                continue;
            }

            if (!TryParseOutputStripRule(trimmed, out var rule))
            {
                _logger.LogWarning(
                    "Skipping unsupported llama output strip pattern '{Pattern}'. Supported format is '<start>[\\s\\S]*?<end>' (or '<start>.*?<end>').",
                    trimmed);
                continue;
            }

            rules.Add(rule);
        }

        return rules;
    }

    private static bool TryParseOutputStripRule(string pattern, out OutputStripRule rule)
    {
        var separator = "[\\s\\S]*?";
        var separatorIndex = pattern.IndexOf(separator, StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            separator = ".*?";
            separatorIndex = pattern.IndexOf(separator, StringComparison.Ordinal);
        }

        if (separatorIndex <= 0)
        {
            rule = default!;
            return false;
        }

        var startPattern = pattern[..separatorIndex];
        var endPattern = pattern[(separatorIndex + separator.Length)..];
        if (string.IsNullOrEmpty(startPattern) || string.IsNullOrEmpty(endPattern))
        {
            rule = default!;
            return false;
        }

        try
        {
            var startToken = Regex.Unescape(startPattern);
            var endToken = Regex.Unescape(endPattern);
            if (string.IsNullOrEmpty(startToken) || string.IsNullOrEmpty(endToken))
            {
                rule = default!;
                return false;
            }

            rule = new OutputStripRule(startToken, endToken);
            return true;
        }
        catch (ArgumentException)
        {
            rule = default!;
            return false;
        }
    }

    private void LogRequestPayload(JsonObject body, string diagnosticInfo)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var config = new JsonObject();
            foreach (var (key, value) in body)
            {
                if (key is "messages" or "tools")
                    continue;
                config[key] = value is null ? null : JsonNode.Parse(value.ToJsonString());
            }

            _logger.LogInformation(
                "LlamaCpp request. {DiagnosticInfo}. Config={RequestConfig}",
                diagnosticInfo,
                config.ToJsonString(RequestJsonOptions));
        }
    }

    private async Task EnsureSuccessWithDiagnosticsAsync(
        HttpResponseMessage response,
        string requestJson,
        string diagnosticInfo,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string responseBody;
        try
        {
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            responseBody = $"<failed to read response body: {ex.GetType().Name}: {ex.Message}>";
        }

        var diagnosticMessage =
            $"llama.cpp chat completion failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase ?? "<none>"}). " +
            $"Endpoint={response.RequestMessage?.RequestUri?.ToString() ?? "<unknown>"}. " +
            $"RequestSummary={diagnosticInfo}. " +
            $"ResponseBody={LimitForLog(responseBody)}. " +
            $"RequestBody={LimitForLog(requestJson)}";

        _logger.LogError("{Message}", diagnosticMessage);
        System.Diagnostics.Trace.TraceError(diagnosticMessage);

        var upstreamExcerpt = ExtractUpstreamDetail(responseBody);

        // Classify 5xx responses so the API/UI can prompt the user to restart the local runtime
        // instead of showing a 1MB HttpRequestException.Message.
        if ((int)response.StatusCode >= 500)
        {
            if (ResponseBodyIndicatesOutOfMemory(responseBody))
            {
                throw new LlamaRuntimeCrashedException(
                    LlamaRuntimeCrashReason.OutOfMemory,
                    "The local model ran out of GPU memory and must be restarted before it can respond again.",
                    response.StatusCode,
                    upstreamExcerpt);
            }

            throw new LlamaRuntimeCrashedException(
                LlamaRuntimeCrashReason.Crashed,
                $"The local model returned HTTP {(int)response.StatusCode} and must be restarted before it can respond again.",
                response.StatusCode,
                upstreamExcerpt);
        }

        // 400 Bad Request with a "no model loaded" body is a precondition failure, not a caller
        // bug: llama-server is up but idle. Classify it so the UI can route to the load dialog
        // through the same pipeline the crash flow uses (see LlamaRuntimeCrashReason.NotReady).
        // Other 4xx (bad tool schema, malformed request, etc.) stay as plain HttpRequestException
        // — we don't want to surface the load dialog for genuine caller bugs.
        if ((int)response.StatusCode == 400
            && ResponseBodyIndicatesRuntimeNotReady(responseBody))
        {
            throw new LlamaRuntimeCrashedException(
                LlamaRuntimeCrashReason.NotReady,
                "The local model runtime has no model loaded. Load a model to continue.",
                response.StatusCode,
                upstreamExcerpt);
        }

        throw new HttpRequestException(
            $"llama.cpp chat completion failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase ?? "<none>"}).",
            null,
            response.StatusCode);
    }

    // Phrases llama-server returns in its 400 JSON error body when /v1/chat/completions hits it
    // with no model loaded. Kept narrow on purpose — a generic "model not found" from a caller
    // supplying a bogus `model` field is a caller bug, not an idle-runtime condition, and must
    // NOT be promoted to NotReady. If upstream wording changes, add the new phrase here.
    private static readonly string[] RuntimeNotReadyMarkers =
    [
        "does not have a model loaded",
        "the server has no model loaded",
        "no model is loaded",
        "model is not loaded"
    ];

    private static bool ResponseBodyIndicatesRuntimeNotReady(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        foreach (var marker in RuntimeNotReadyMarkers)
        {
            if (body.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // CUDA / llama.cpp OOM markers observed across upstream builds. Matching is case-insensitive
    // and the body is already capped to ~8KB by the diagnostic path above, so this is safe to run
    // on the (possibly large) raw response body.
    private static readonly string[] OutOfMemoryMarkers =
    [
        "CUDA error: out of memory",
        "cudaErrorMemoryAllocation",
        "cudaMalloc failed",
        "ggml_cuda_host_malloc: failed",
        "failed to allocate CUDA",
        "failed to allocate buffer",
        "failed to allocate compute buffers",
        "std::bad_alloc",
        "out of memory"
    ];

    private static bool ResponseBodyIndicatesOutOfMemory(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        foreach (var marker in OutOfMemoryMarkers)
        {
            if (body.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ExtractUpstreamDetail(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        const int maxChars = 500;
        var trimmed = body.Trim();

        // Try to pull an "error.message" field out of a JSON error body first — that gives us
        // the cleanest single-sentence signal without whatever giant request echo follows.
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var errorEl))
            {
                if (errorEl.ValueKind == JsonValueKind.String)
                {
                    return Clip(errorEl.GetString(), maxChars);
                }

                if (errorEl.ValueKind == JsonValueKind.Object
                    && errorEl.TryGetProperty("message", out var msgEl)
                    && msgEl.ValueKind == JsonValueKind.String)
                {
                    return Clip(msgEl.GetString(), maxChars);
                }
            }

            // Some llama-server builds return {"message":"..."} without an "error" object.
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out var rootMsgEl)
                && rootMsgEl.ValueKind == JsonValueKind.String)
            {
                return Clip(rootMsgEl.GetString(), maxChars);
            }
        }
        catch (JsonException)
        {
        }

        return Clip(trimmed, maxChars);
    }

    private static string? Clip(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= maxChars ? value : value[..maxChars] + "…";
    }

    private static string LimitForLog(string value)
    {
        const int maxChars = 8_000;
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (value.Length <= maxChars)
        {
            return value;
        }

        return $"{value[..maxChars]}... [truncated {value.Length - maxChars} chars]";
    }

    private HttpRequestMessage BuildHttpRequest(string requestJson)
    {
        if (string.IsNullOrWhiteSpace(_config.BaseUrl))
        {
            throw new InvalidOperationException("LlamaCpp BaseUrl is not configured.");
        }

        var endpoint = $"{_config.BaseUrl!.TrimEnd('/')}/v1/chat/completions";
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        }

        return request;
    }

    private (JsonObject Body, string DiagnosticInfo) BuildRequestBody(ChatCompletionRequest request, bool stream)
    {
        var combineSystem = _profileData?.CombineSystemAndDeveloperMessages ?? true;
        var mappedMessages = combineSystem
            ? NormalizeMessagesWithCombinedSystemPrompt(request.Messages)
            : request.Messages.Select(MapMessage).ToList();

        var modelId = _deploymentId ?? request.Model;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException("LlamaCpp model is required. Provide an explicit model deployment id.");
        }

        var body = new JsonObject
        {
            ["model"] = modelId,
            ["messages"] = JsonSerializer.SerializeToNode(mappedMessages, RequestJsonOptions),
            ["stream"] = stream
        };

        if (stream)
        {
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        if (request.Tools is { Count: > 0 })
        {
            var mappedTools = request.Tools.Select(MapTool).ToList();
            body["tools"] = JsonSerializer.SerializeToNode(mappedTools, RequestJsonOptions);
            body["parallel_tool_calls"] = _parallelToolCalls;
        }

        MergeSamplingParameters(body, request);

        if (_profileData?.ThinkingControl != null)
        {
            ApplyThinkingControl(body, mappedMessages, _profileData.ThinkingControl, request.ReasoningEffort);
        }

        var diagnosticInfo = BuildDiagnosticInfo(modelId, stream, mappedMessages, request.Tools?.Count ?? 0);
        return (body, diagnosticInfo);
    }

    private void MergeSamplingParameters(JsonObject body, ChatCompletionRequest request)
    {
        if (_profileData?.SamplingDefaults != null)
        {
            foreach (var (key, defaultValue) in _profileData.SamplingDefaults)
            {
                body[key] = defaultValue;
            }
        }

        if (request.SamplingParameters != null)
        {
            foreach (var (key, value) in request.SamplingParameters)
            {
                body[key] = value;
            }
        }
    }

    private static void ApplyThinkingControl(
        JsonObject request,
        List<LlamaMessage> messages,
        ThinkingControl thinkingControl,
        string? reasoningEffort)
    {
        var choice = reasoningEffort ?? thinkingControl.DefaultChoice;
        if (string.IsNullOrWhiteSpace(choice) || thinkingControl.ChoiceActions == null || thinkingControl.ChoiceActions.Count == 0)
        {
            return;
        }

        var normalizedChoice = choice.ToLowerInvariant();
        var matchingKey = thinkingControl.ChoiceActions.Keys
            .FirstOrDefault(k => string.Equals(k, normalizedChoice, StringComparison.OrdinalIgnoreCase));

        if (matchingKey == null || !thinkingControl.ChoiceActions.TryGetValue(matchingKey, out var actions))
        {
            throw new InvalidOperationException(
                $"Reasoning choice '{choice}' is not defined in the profile.");
        }

        foreach (var action in actions)
        {
            switch (action.Target)
            {
                case ThinkingActionTarget.RequestField:
                    request[action.Key] = JsonValueFromObject(action.Value!);
                    break;
                case ThinkingActionTarget.NestedRequestField:
                    SetNestedField(request, action.Key, action.Value!);
                    break;
                case ThinkingActionTarget.SystemMessagePrefix:
                    PrependToSystemMessage(messages, action.Value?.ToString() ?? string.Empty);
                    break;
            }
        }
    }

    private static JsonNode? JsonValueFromObject(object value) => value switch
    {
        bool b => JsonValue.Create(b),
        string s => JsonValue.Create(s),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create(f),
        JsonElement je => JsonNode.Parse(je.GetRawText()),
        _ => JsonValue.Create(value?.ToString())
    };

    private static void SetNestedField(JsonObject root, string dottedKey, object value)
    {
        var parts = dottedKey.Split('.');
        var current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!current.ContainsKey(parts[i]) || current[parts[i]] is not JsonObject nested)
            {
                nested = new JsonObject();
                current[parts[i]] = nested;
            }
            else
            {
                current = nested;
                continue;
            }
            current = nested;
        }
        current[parts[^1]] = JsonValueFromObject(value);
    }

    private static void PrependToSystemMessage(List<LlamaMessage> messages, string prefix)
    {
        var system = messages.FirstOrDefault(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase));
        if (system != null)
        {
            var existingText = system.Content?.ToString() ?? string.Empty;
            system.Content = prefix + existingText;
        }
        else
        {
            messages.Insert(0, new LlamaMessage { Role = "system", Content = prefix });
        }
    }

    private static string BuildDiagnosticInfo(string modelId, bool stream, List<LlamaMessage> messages, int toolCount)
    {
        var systemCount = 0;
        var userCount = 0;
        var assistantCount = 0;
        var toolMsgCount = 0;

        foreach (var msg in messages)
        {
            switch (msg.Role)
            {
                case "system": systemCount++; break;
                case "user": userCount++; break;
                case "assistant": assistantCount++; break;
                case "tool": toolMsgCount++; break;
            }
        }

        return $"model={modelId},stream={stream},messages={messages.Count},system={systemCount},user={userCount},assistant={assistantCount},tool={toolMsgCount},tools={toolCount}";
    }

    private static List<LlamaMessage> NormalizeMessagesWithCombinedSystemPrompt(IReadOnlyList<ChatMessage> messages)
    {
        var normalized = new List<LlamaMessage>();
        var systemFragments = new List<string>();

        foreach (var message in messages)
        {
            if (message.Role is ChatRole.System or ChatRole.Developer)
            {
                var text = message.GetText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    systemFragments.Add(text);
                }

                continue;
            }

            normalized.Add(MapMessage(message));
        }

        if (systemFragments.Count > 0)
        {
            normalized.Insert(0, new LlamaMessage
            {
                Role = "system",
                Content = string.Join("\n\n", systemFragments)
            });
        }

        return normalized;
    }

    private static LlamaMessage MapMessage(ChatMessage message)
    {
        if (message.Role == ChatRole.Tool)
        {
            return new LlamaMessage
            {
                Role = "tool",
                Content = message.GetText(),
                ToolCallId = message.ToolCallId
            };
        }

        var mapped = new LlamaMessage
        {
            Role = MapRole(message.Role),
            Content = MapContent(message.Content)
        };

        if (message.ToolCalls is { Count: > 0 })
        {
            mapped.ToolCalls = message.ToolCalls.Select(tc => new LlamaToolCall
            {
                Id = tc.Id,
                Type = "function",
                Function = new LlamaFunctionCall
                {
                    Name = tc.Function.Name,
                    Arguments = NormalizeArgumentsForOutgoing(tc.Function.Arguments)
                }
            }).ToList();
        }

        return mapped;
    }

    private static object MapContent(IReadOnlyList<ChatContent> contentItems)
    {
        if (contentItems == null || contentItems.Count == 0)
        {
            return string.Empty;
        }

        var hasImage = contentItems.Any(item => item.ImageUrl != null);
        if (!hasImage)
        {
            return string.Concat(contentItems
                .Where(item => !string.IsNullOrEmpty(item.Text))
                .Select(item => item.Text));
        }

        var parts = new List<object>();
        foreach (var item in contentItems)
        {
            if (!string.IsNullOrEmpty(item.Text))
            {
                parts.Add(new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = item.Text
                });
            }

            if (item.ImageUrl != null)
            {
                parts.Add(new Dictionary<string, object?>
                {
                    ["type"] = "image_url",
                    ["image_url"] = new Dictionary<string, object?>
                    {
                        ["url"] = item.ImageUrl.Url
                    }
                });
            }
        }

        return parts.Count > 0 ? parts : string.Empty;
    }

    private static string NormalizeArgumentsForOutgoing(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.String)
        {
            return arguments.GetString() ?? "{}";
        }

        if (arguments.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "{}";
        }

        return arguments.GetRawText();
    }

    private static LlamaToolDefinition MapTool(ChatToolDefinition definition)
    {
        if (definition.Function == null)
        {
            throw new InvalidOperationException("Function tool definition is required.");
        }

        return new LlamaToolDefinition
        {
            Type = "function",
            Function = new LlamaToolFunctionDefinition
            {
                Name = definition.Function.Name,
                Description = definition.Function.Description,
                Parameters = definition.Function.Parameters
            }
        };
    }

    private static string MapRole(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.Developer => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        ChatRole.Tool => "tool",
        _ => "user"
    };

    private static ChatCompletionResponse ParseCompletionResponse(
        JsonElement root,
        IReadOnlyList<OutputStripRule> assistantOutputStripRules)
    {
        var choice = root.TryGetProperty("choices", out var choicesEl)
                     && choicesEl.ValueKind == JsonValueKind.Array
                     && choicesEl.GetArrayLength() > 0
            ? choicesEl[0]
            : throw new InvalidOperationException("llama.cpp response did not include choices.");

        var finishReason = choice.TryGetProperty("finish_reason", out var finishEl)
            && finishEl.ValueKind == JsonValueKind.String
            ? finishEl.GetString()
            : null;

        if (!choice.TryGetProperty("message", out var messageEl) || messageEl.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("llama.cpp response did not include message.");
        }

        var text = StripLeadingEmptyBlocks(
            ExtractText(messageEl.TryGetProperty("content", out var contentEl) ? contentEl : default),
            assistantOutputStripRules,
            finalize: true,
            out _);

        var reasoningText = ExtractText(
            messageEl.TryGetProperty("reasoning_content", out var reasoningEl) ? reasoningEl : default);

        var toolCalls = messageEl.TryGetProperty("tool_calls", out var toolCallsEl)
            ? ParseToolCalls(toolCallsEl)
            : [];

        var content = string.IsNullOrWhiteSpace(text)
            ? []
            : new List<ChatContent> { new(text) };

        IReadOnlyList<ChatThinkingBlock>? thinkingBlocks = null;
        if (!string.IsNullOrWhiteSpace(reasoningText))
        {
            thinkingBlocks = [ChatThinkingBlock.ForThinking(reasoningText, string.Empty)];
        }

        ChatMessage message;
        if (toolCalls.Count > 0 || thinkingBlocks != null)
        {
            message = new ChatMessage(ChatRole.Assistant, content, toolCalls, thinkingBlocks);
        }
        else
        {
            message = new ChatMessage(ChatRole.Assistant, content);
        }

        var normalizedFinishReason = !string.IsNullOrWhiteSpace(finishReason)
            ? finishReason
            : toolCalls.Count > 0 ? "tool_calls" : "stop";

        var usage = ParseUsage(root);

        return new ChatCompletionResponse(
            [new ChatChoice(message, normalizedFinishReason)],
            usage);
    }

    private static string StripLeadingEmptyBlocks(
        string text,
        IReadOnlyList<OutputStripRule> rules,
        bool finalize,
        out bool resolved)
    {
        if (string.IsNullOrEmpty(text) || rules.Count == 0)
        {
            resolved = true;
            return text;
        }

        var cursor = 0;
        var removedAny = false;

        while (true)
        {
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
            {
                cursor++;
            }

            if (cursor >= text.Length)
            {
                resolved = true;
                return string.Empty;
            }

            OutputStripRule? matchedRule = null;
            foreach (var rule in rules)
            {
                if (text.AsSpan(cursor).StartsWith(rule.StartToken.AsSpan(), StringComparison.Ordinal))
                {
                    matchedRule = rule;
                    break;
                }
            }

            if (matchedRule == null)
            {
                if (!finalize)
                {
                    var remaining = text.Length - cursor;
                    foreach (var rule in rules)
                    {
                        if (remaining > 0 && remaining < rule.StartToken.Length
                            && rule.StartToken.AsSpan(0, remaining).SequenceEqual(text.AsSpan(cursor, remaining)))
                        {
                            resolved = false;
                            return string.Empty;
                        }
                    }
                }

                resolved = true;
                return removedAny ? text[cursor..] : text;
            }

            var contentStart = cursor + matchedRule.StartToken.Length;
            var endIndex = text.IndexOf(matchedRule.EndToken, contentStart, StringComparison.Ordinal);
            if (endIndex < 0)
            {
                if (!finalize)
                {
                    resolved = false;
                    return string.Empty;
                }

                resolved = true;
                return removedAny ? text[cursor..] : text;
            }

            var emptyInnerBlock = true;
            for (var i = contentStart; i < endIndex; i++)
            {
                if (!char.IsWhiteSpace(text[i]))
                {
                    emptyInnerBlock = false;
                    break;
                }
            }

            if (!emptyInnerBlock)
            {
                resolved = true;
                return removedAny ? text[cursor..] : text;
            }

            removedAny = true;
            cursor = endIndex + matchedRule.EndToken.Length;
        }
    }

    private static List<ChatToolCall> ParseToolCalls(JsonElement toolCallsEl)
    {
        var list = new List<ChatToolCall>();
        if (toolCallsEl.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var toolCallEl in toolCallsEl.EnumerateArray())
        {
            if (toolCallEl.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = toolCallEl.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() ?? Guid.NewGuid().ToString("N")
                : Guid.NewGuid().ToString("N");

            var functionName = string.Empty;
            JsonElement arguments = JsonSerializer.SerializeToElement(new Dictionary<string, object>());

            if (toolCallEl.TryGetProperty("function", out var functionEl) && functionEl.ValueKind == JsonValueKind.Object)
            {
                if (functionEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                {
                    functionName = nameEl.GetString() ?? string.Empty;
                }

                if (functionEl.TryGetProperty("arguments", out var argsEl))
                {
                    arguments = ParseToolArguments(argsEl);
                }
            }

            list.Add(new ChatToolCall
            {
                Id = id,
                Type = "function",
                Function = new ChatToolCallFunction
                {
                    Name = functionName,
                    Arguments = arguments
                }
            });
        }

        return list;
    }

    private static JsonElement ParseToolArguments(JsonElement argsEl)
    {
        if (argsEl.ValueKind == JsonValueKind.Object)
        {
            return JsonSerializer.SerializeToElement(argsEl);
        }

        if (argsEl.ValueKind == JsonValueKind.String)
        {
            var value = argsEl.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                return JsonSerializer.SerializeToElement(new Dictionary<string, object>());
            }

            try
            {
                using var doc = JsonDocument.Parse(value);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return JsonSerializer.SerializeToElement(doc.RootElement);
                }
            }
            catch
            {
            }
        }

        return JsonSerializer.SerializeToElement(new Dictionary<string, object>());
    }

    private static ChatCompletionUsage? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usageEl) || usageEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var promptTokens = GetInt32(usageEl, "prompt_tokens");
        var completionTokens = GetInt32(usageEl, "completion_tokens");
        var totalTokens = GetInt32(usageEl, "total_tokens");

        ChatPromptTokensDetails? details = null;
        if (usageEl.TryGetProperty("prompt_tokens_details", out var promptDetailsEl) && promptDetailsEl.ValueKind == JsonValueKind.Object)
        {
            details = new ChatPromptTokensDetails
            {
                CachedTokens = GetInt32(promptDetailsEl, "cached_tokens")
            };
        }

        return new ChatCompletionUsage
        {
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = totalTokens,
            PromptTokensDetails = details
        };
    }

    private static int? GetInt32(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var el))
        {
            return null;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var number))
        {
            return number;
        }

        return null;
    }

    private static string ExtractText(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return string.Empty;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? string.Empty;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var item in element.EnumerateArray())
            {
                var value = ExtractText(item);
                if (!string.IsNullOrEmpty(value))
                {
                    builder.Append(value);
                }
            }

            return builder.ToString();
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
            {
                return textEl.GetString() ?? string.Empty;
            }

            if (element.TryGetProperty("content", out var contentEl))
            {
                return ExtractText(contentEl);
            }
        }

        return string.Empty;
    }

    private sealed class StreamingAccumulator
    {
        private readonly IReadOnlyList<OutputStripRule> _assistantOutputStripRules;
        private readonly StringBuilder _assistantRawText = new();
        private readonly StringBuilder _assistantText = new();
        private readonly StringBuilder _reasoningText = new();
        private readonly Dictionary<int, ToolCallAccumulator> _toolCalls = [];
        private ChatCompletionUsage? _usage;
        private string? _finishReason;
        private int _assistantFilteredLength;

        public StreamingAccumulator(IReadOnlyList<OutputStripRule> assistantOutputStripRules)
        {
            _assistantOutputStripRules = assistantOutputStripRules;
        }

        public void ApplyChunk(JsonElement chunk, Action<ChatCompletionChunk> onChunk)
        {
            if (chunk.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
            {
                _usage = ParseUsage(chunk);
            }

            if (!chunk.TryGetProperty("choices", out var choicesEl) || choicesEl.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var choiceEl in choicesEl.EnumerateArray())
            {
                if (choiceEl.TryGetProperty("finish_reason", out var finishReasonEl) && finishReasonEl.ValueKind == JsonValueKind.String)
                {
                    _finishReason = finishReasonEl.GetString();
                }

                if (!choiceEl.TryGetProperty("delta", out var deltaEl) || deltaEl.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (deltaEl.TryGetProperty("reasoning_content", out var reasoningEl))
                {
                    var reasoningDelta = ExtractText(reasoningEl);
                    if (!string.IsNullOrEmpty(reasoningDelta))
                    {
                        _reasoningText.Append(reasoningDelta);
                        EmitDelta(onChunk, reasoningDelta, finishReason: "thinking");
                    }
                }

                if (deltaEl.TryGetProperty("content", out var contentEl))
                {
                    var textDelta = ExtractText(contentEl);
                    if (!string.IsNullOrEmpty(textDelta))
                    {
                        _assistantRawText.Append(textDelta);
                        var filteredDelta = GetAssistantDelta(finalize: false);
                        if (!string.IsNullOrEmpty(filteredDelta))
                        {
                            EmitDelta(onChunk, filteredDelta, finishReason: null);
                        }
                    }
                }

                if (deltaEl.TryGetProperty("tool_calls", out var toolCallsEl))
                {
                    AppendToolCalls(toolCallsEl);
                }
            }
        }

        public void FlushPendingAssistantText(Action<ChatCompletionChunk> onChunk)
        {
            var filteredDelta = GetAssistantDelta(finalize: true);
            if (!string.IsNullOrEmpty(filteredDelta))
            {
                EmitDelta(onChunk, filteredDelta, finishReason: null);
            }
        }

        public ChatCompletionResponse ToResponse()
        {
            _ = GetAssistantDelta(finalize: true);

            var text = _assistantText.ToString();
            var content = string.IsNullOrWhiteSpace(text)
                ? []
                : new List<ChatContent> { new(text) };

            IReadOnlyList<ChatThinkingBlock>? thinkingBlocks = null;
            var reasoning = _reasoningText.ToString();
            if (!string.IsNullOrWhiteSpace(reasoning))
            {
                thinkingBlocks = [ChatThinkingBlock.ForThinking(reasoning, string.Empty)];
            }

            var toolCalls = _toolCalls
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => kvp.Value.ToToolCall())
                .ToList();

            var message = toolCalls.Count > 0 || thinkingBlocks != null
                ? new ChatMessage(ChatRole.Assistant, content, toolCalls, thinkingBlocks)
                : new ChatMessage(ChatRole.Assistant, content);

            var finishReason = !string.IsNullOrWhiteSpace(_finishReason)
                ? _finishReason
                : toolCalls.Count > 0 ? "tool_calls" : "stop";

            return new ChatCompletionResponse(
                [new ChatChoice(message, finishReason)],
                _usage);
        }

        private string GetAssistantDelta(bool finalize)
        {
            var rawText = _assistantRawText.ToString();
            if (rawText.Length == 0)
            {
                return string.Empty;
            }

            var filteredText = StripLeadingEmptyBlocks(
                rawText,
                _assistantOutputStripRules,
                finalize,
                out var resolved);

            if (!resolved && !finalize)
            {
                return string.Empty;
            }

            if (filteredText.Length <= _assistantFilteredLength)
            {
                return string.Empty;
            }

            var delta = filteredText[_assistantFilteredLength..];
            _assistantFilteredLength = filteredText.Length;
            _assistantText.Append(delta);
            return delta;
        }

        private void AppendToolCalls(JsonElement toolCallsEl)
        {
            if (toolCallsEl.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var fallbackIndex = 0;
            foreach (var callEl in toolCallsEl.EnumerateArray())
            {
                if (callEl.ValueKind != JsonValueKind.Object)
                {
                    fallbackIndex++;
                    continue;
                }

                var index = callEl.TryGetProperty("index", out var indexEl) && indexEl.ValueKind == JsonValueKind.Number
                            && indexEl.TryGetInt32(out var parsedIndex)
                    ? parsedIndex
                    : fallbackIndex;

                if (!_toolCalls.TryGetValue(index, out var accumulator))
                {
                    accumulator = new ToolCallAccumulator();
                    _toolCalls[index] = accumulator;
                }

                if (callEl.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                {
                    accumulator.Id = idEl.GetString() ?? accumulator.Id;
                }

                if (callEl.TryGetProperty("function", out var functionEl) && functionEl.ValueKind == JsonValueKind.Object)
                {
                    if (functionEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    {
                        accumulator.Name = nameEl.GetString() ?? accumulator.Name;
                    }

                    if (functionEl.TryGetProperty("arguments", out var argsEl))
                    {
                        accumulator.AppendArguments(argsEl);
                    }
                }

                fallbackIndex++;
            }
        }

        private static void EmitDelta(Action<ChatCompletionChunk> onChunk, string text, string? finishReason)
        {
            var delta = new ChatDelta(ChatRole.Assistant, text);
            onChunk(new ChatCompletionChunk([new ChatChoiceDelta(delta, finishReason)]));
        }

        private sealed class ToolCallAccumulator
        {
            private readonly StringBuilder _arguments = new();

            public string Id { get; set; } = Guid.NewGuid().ToString("N");
            public string Name { get; set; } = string.Empty;

            public void AppendArguments(JsonElement argsEl)
            {
                if (argsEl.ValueKind == JsonValueKind.String)
                {
                    _arguments.Append(argsEl.GetString());
                    return;
                }

                if (argsEl.ValueKind == JsonValueKind.Object)
                {
                    _arguments.Append(argsEl.GetRawText());
                }
            }

            public ChatToolCall ToToolCall()
            {
                var arguments = ParseArguments(_arguments.ToString());
                return new ChatToolCall
                {
                    Id = Id,
                    Type = "function",
                    Function = new ChatToolCallFunction
                    {
                        Name = Name,
                        Arguments = arguments
                    }
                };
            }

            private static JsonElement ParseArguments(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return JsonSerializer.SerializeToElement(new Dictionary<string, object>());
                }

                try
                {
                    using var doc = JsonDocument.Parse(value);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        return JsonSerializer.SerializeToElement(doc.RootElement);
                    }
                }
                catch
                {
                }

                return JsonSerializer.SerializeToElement(new Dictionary<string, object>());
            }
        }
    }

    private sealed record OutputStripRule(string StartToken, string EndToken);

    private sealed class LlamaMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "user";

        [JsonPropertyName("content")]
        public object? Content { get; set; }

        [JsonPropertyName("tool_call_id")]
        public string? ToolCallId { get; set; }

        [JsonPropertyName("tool_calls")]
        public List<LlamaToolCall>? ToolCalls { get; set; }
    }

    private sealed class LlamaToolDefinition
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        [JsonPropertyName("function")]
        public LlamaToolFunctionDefinition Function { get; set; } = new();
    }

    private sealed class LlamaToolFunctionDefinition
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("parameters")]
        public JsonNode? Parameters { get; set; }
    }

    private sealed class LlamaToolCall
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        [JsonPropertyName("function")]
        public LlamaFunctionCall Function { get; set; } = new();
    }

    private sealed class LlamaFunctionCall
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("arguments")]
        public string Arguments { get; set; } = "{}";
    }
}
