using AntRunner.ToolCalling;
using AntRunner.ToolCalling.AssistantDefinitions;
using AntRunner.ToolCalling.AssistantDefinitions.Storage;
using AntRunner.ToolCalling.Functions;
using AntRunner.Chat.Abstractions;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace AntRunner.Chat
{
    /// <summary>
    /// Internal execution engine for running assistant threads.
    /// Extracted from ChatRunner to centralize execution logic.
    /// </summary>
    public static class ThreadRun
    {
        static readonly HttpClient _httpClient = HttpClientUtility.Get();

        private static readonly ConcurrentDictionary<string, Dictionary<string, ToolCaller>> RequestBuilderCache = new();

        // Tracks which files have already been announced in a conversation to avoid duplicates
        private static readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> ConversationFileAnnouncements = new();

        private static bool AssistantHasFilesContextOption(AssistantDefinition def)
        {
            if (def.ContextOptions == null) return false;
            return def.ContextOptions.Any(kv => kv.Value != null && kv.Value.Contains("[@files]", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Formats new and modified file paths as a console-style code block for improved LLM attention.
        /// </summary>
        private static string FormatFileChangesConsole(List<string> newFiles, List<string> modifiedFiles)
        {
            var sb = new StringBuilder();
            sb.AppendLine("```console");
            if (newFiles.Count > 0)
            {
                sb.AppendLine("# New Files");
                foreach (var p in newFiles)
                {
                    sb.AppendLine(p);
                }
            }
            if (modifiedFiles.Count > 0)
            {
                if (newFiles.Count > 0) sb.AppendLine();
                sb.AppendLine("# Modified Files");
                foreach (var p in modifiedFiles)
                {
                    sb.AppendLine(p);
                }
            }
            sb.Append("```");
            return sb.ToString();
        }

        /// <summary>
        /// Generates a cache key for RequestBuilderCache.
        /// RULE: Internally, we use string names for resolution always.
        /// </summary>
        private static string GenerateRequestBuilderCacheKey(string assistantName)
        {
            return assistantName;
        }

        /// <summary>
        /// Clears the RequestBuilderCache for a specific assistant.
        /// Call this when an assistant's OpenAPI schemas are updated to force reload from database.
        /// </summary>
        /// <param name="assistantName">The name of the assistant to clear</param>
        public static void ClearRequestBuilderCache(string assistantName)
        {
            var cacheKey = GenerateRequestBuilderCacheKey(assistantName);
            RequestBuilderCache.TryRemove(cacheKey, out _);
        }

        /// <summary>
        /// Clears all cached request builders.
        /// Useful for testing or when bulk updates are made to assistants.
        /// </summary>
        public static void ClearAllRequestBuilderCache()
        {
            RequestBuilderCache.Clear();
        }

        /// <summary>
        /// Normalizes assistant-generated text emitted by the LLM stream.
        /// Currently replaces Unicode em-dash (\u2014) with ASCII hyphen-minus ('-').
        /// Applied only to assistant text at the LLM boundary (streaming deltas and final assistant message).
        /// </summary>
        private static string NormalizeAssistantText(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }

            return text.Replace("\u2014", "\u2013");
        }

        /// <summary>
        /// Core execution engine for running assistant threads.
        /// </summary>
        /// <param name="isAgentInvocation">If true, seeds messages as System → previous → User. Previous must contain only current-turn attachment messages.</param>
        public static Task<ChatRunOutput?> ExecuteAsync(
            ChatRunOptions options,
            IChatCompletionClientFactory clientFactory,
            List<ChatMessage>? previous,
            HttpClient? httpClient,
            MessageAddedEventHandler? onMessage,
            StreamingMessageProgressEventHandler? onStream,
            InvocationContext? ctx,
            CancellationToken token,
            bool isAgentInvocation = false,
            string? contextMessage = null)
        {
            // Backward-compatible overload that forwards to the extended signature
            return ExecuteAsync(
                options,
                clientFactory,
                previous,
                httpClient,
                onMessage,
                onStream,
                onExternalToolCall: null,
                resumeWithoutNewUserMessage: false,
                ctx,
                token,
                isAgentInvocation,
                contextMessage);
        }

        /// <summary>
        /// Core execution engine for running assistant threads.
        /// </summary>
        /// <param name="isAgentInvocation">If true, seeds messages as System → previous → User. Previous must contain only current-turn attachment messages.</param>
        /// <param name="contextMessage">Optional context options message to inject before assistant instructions.</param>
        public static async Task<ChatRunOutput?> ExecuteAsync(
            ChatRunOptions options,
            IChatCompletionClientFactory clientFactory,
            List<ChatMessage>? previous,
            HttpClient? httpClient,
            MessageAddedEventHandler? onMessage,
            StreamingMessageProgressEventHandler? onStream,
            ExternalToolCallEventHandler? onExternalToolCall,
            bool resumeWithoutNewUserMessage,
            InvocationContext? ctx,
            CancellationToken token,
            bool isAgentInvocation = false,
            string? contextMessage = null)
        {
            // Retrieve the assistant ID using the assistant name from the configuration
            ArgumentNullException.ThrowIfNull(ctx);

            var assistantDef = await AssistantUtility.GetAssistantCreateRequest(options.AssistantName) ?? throw new Exception($"Can't find assistant definition for '{options.AssistantName}'");

            if (options.OverrideTemperature.HasValue)
            {
                assistantDef.Temperature = options.OverrideTemperature;
            }

            if (options.OverrideTopP.HasValue)
            {
                assistantDef.TopP = options.OverrideTopP.Value;
            }

            if (!string.IsNullOrEmpty(options.OverrideReasoningEffort))
            {
                assistantDef.ReasoningEffort = options.OverrideReasoningEffort;
            }

            // 256000 is the maximum instruction length allowed by the API
            if (options.Instructions.Length >= 256000)
            {
                TraceWarning("Instructions are too long, truncating");
                options.Instructions = options.Instructions[..255999];
            }

            // Prefer an explicit DeploymentId from the host (already resolved by IChatModelResolver in the API)
            // over the raw assistant manifest model so global default / override semantics apply consistently.
            options.DeploymentId = options.DeploymentId ?? assistantDef.Model ?? clientFactory.DefaultDeploymentId;
            var resolvedModelId = options.DeploymentId ?? assistantDef.Model;
            var reasoningEffortParam = await ResolveReasoningEffortAsync(
                resolvedModelId,
                assistantDef.ReasoningEffort,
                token);
            var suppressSamplingForReasoning =
                !string.IsNullOrWhiteSpace(reasoningEffortParam)
                && SupportsOpenAiReasoningEffortByModelId(resolvedModelId);

            var api = clientFactory.CreateClient(options.DeploymentId, httpClient);

            var messages = new List<ChatMessage>();
            var hasKnowledge = assistantDef.Tools?.FirstOrDefault(t => t.Type == "file_search") != null;

            if (isAgentInvocation)
            {
                // Agent invocation: System instruction(s) → optional knowledge hint → Context Options → previous (attachments) → User

                if (!string.IsNullOrEmpty(assistantDef.Instructions))
                {
                    messages.Add(new ChatMessage(ChatRole.System, assistantDef.Instructions));
                }

                if (hasKnowledge)
                {
                    messages.Add(new ChatMessage(ChatRole.System, "Use SearchAssistantFiles for extended instructions and guidance on performing tasks"));
                }

                // Context options come AFTER primary system prompts to improve LLM attention
                if (!string.IsNullOrEmpty(contextMessage))
                {
                    messages.Add(new ChatMessage(ChatRole.System, contextMessage));
                }

                if (messages.Count > 0)
                {
                    onMessage?.Invoke(null, new MessageAddedEventArgs(messages.Last().Role.ToString(), messages.Last().GetText()));
                }

                if (previous != null && previous.Count > 0)
                {
                    foreach (var previousMessage in previous)
                    {
                        messages.Add(previousMessage);
                    }
                }

                messages.Add(new ChatMessage(ChatRole.User, options.Instructions));
                onMessage?.Invoke(null, new MessageAddedEventArgs(messages.Last().Role.ToString(), messages.Last().GetText()));
            }
            else if (previous != null && previous.Count > 0)
            {
                foreach (var previousMessage in previous)
                {
                    messages.Add(previousMessage);
                }
                if (!resumeWithoutNewUserMessage)
                {
                    messages.Add(new ChatMessage(ChatRole.User, options.Instructions));
                    onMessage?.Invoke(null, new MessageAddedEventArgs(messages.Last().Role.ToString(), messages.Last().GetText()));
                }
            }
            else
            {
                messages =
                [
                    new ChatMessage(ChatRole.System, !string.IsNullOrEmpty(assistantDef.Instructions) ? assistantDef.Instructions : "You are a helpful assistant"),
                ];
                if (hasKnowledge)
                {
                    messages.Add(new ChatMessage(ChatRole.System, "Use SearchAssistantFiles for extended instructions and reference guidance"));
                }
                messages.Add(new ChatMessage(ChatRole.User, options.Instructions));

                onMessage?.Invoke(null, new MessageAddedEventArgs(messages.First().Role.ToString(), messages.First().GetText()));
                onMessage?.Invoke(null, new MessageAddedEventArgs(messages.Last().Role.ToString(), messages.Last().GetText()));
            }

            var tools = new List<ChatToolDefinition>();

            if (assistantDef.Tools != null)
            {
                foreach (var toolDef in assistantDef.Tools.Where(t => t.Type == "function"))
                {
                    if (toolDef.Function?.AsObject != null)
                    {
                        var function = toolDef.Function.AsObject;
                        var functionParametersJsonNode = JsonNode.Parse(JsonSerializer.Serialize(function.Parameters));

                        var newFunction = new ChatFunctionDefinition(function.Name!, function.Description, functionParametersJsonNode);
                        var newTool = new ChatToolDefinition(newFunction);
                        tools.Add(newTool);
                    }
                }
            }

            bool continueChat = true;

            ChatChoice? choice = null;
            ChatRunOutput? runResults = null;
            int evaluatorTurnCounter = 0;
            
            // Track files created/modified across all tool calls in this run
            var accumulatedNewFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var accumulatedModifiedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                while (continueChat)
                {
                    token.ThrowIfCancellationRequested();
                    var tempParam = suppressSamplingForReasoning ? null : assistantDef.Temperature;
                    var topPParam = suppressSamplingForReasoning ? null : assistantDef.TopP;

                    var chatRequest = new ChatCompletionRequest(messages, tools: tools, model: options.DeploymentId, temperature: tempParam, topP: topPParam, reasoningEffort: reasoningEffortParam, samplingParameters: assistantDef.SamplingParameters);

                    ChatCompletionResponse response;
                    if (onStream != null)
                    {
                        response = await GetCompletionAndStreamAsync(api, chatRequest, onStream, token);
                    }
                    else
                    {
                        response = await api.GetCompletionAsync(chatRequest, token);
                    }

                    messages.Add(response.FirstChoice!.Message);

                    string? toolCallJson = null;
                    if (response.FirstChoice.Message.ToolCalls != null && response.FirstChoice.Message.ToolCalls.Count > 0)
                    {
                        toolCallJson = JsonSerializer.Serialize(response.FirstChoice.Message.ToolCalls);
                    }

                    var lastRole = messages.Last().Role;
                    var lastText = messages.Last().GetText();
                    if (lastRole == ChatRole.Assistant)
                    {
                        lastText = NormalizeAssistantText(lastText);
                    }
                    onMessage?.Invoke(null, new MessageAddedEventArgs(
                        lastRole.ToString(),
                        lastText,
                        null,
                        null,
                        toolCallJson));
                    choice = response.FirstChoice;

                    switch (choice.FinishReason)
                    {
                        case "stop":
                            continueChat = false;
                            break;
                        case "tool_calls":
                            {
                                // Partition tool calls into client-handled vs server-handled based on ActionType
                                await EnsureRequestBuilderCache(assistantDef.Name!);
                                var cacheKeyPartition = GenerateRequestBuilderCacheKey(assistantDef.Name!);
                                if (!RequestBuilderCache.TryGetValue(cacheKeyPartition, out var buildersPartition) || buildersPartition.Count == 0)
                                {
                                    // If no builders available, fall back to executing as server-handled
                                    var (newFiles, modifiedFiles) = await DoToolCalls(
                                        assistantDef,
                                        choice.Message.ToolCalls!,
                                        messages,
                                        oAuthUserAccessToken: options.oAuthUserAccessToken,
                                        httpClient: httpClient,
                                        messageAdded: onMessage,
                                        ctx: ctx,
                                        cancellationToken: token);
                                    foreach (var f in newFiles) accumulatedNewFiles.Add(f);
                                    foreach (var f in modifiedFiles) accumulatedModifiedFiles.Add(f);
                                    break;
                                }

                                var clientHandled = new List<ChatToolCall>();
                                var serverHandled = new List<ChatToolCall>();
                                foreach (var tc in choice.Message.ToolCalls!)
                                {
                                    if (!tc.IsFunction)
                                    {
                                        serverHandled.Add(tc);
                                        continue;
                                    }
                                    if (buildersPartition.TryGetValue(tc.Function.Name, out var b))
                                    {
                                        if (b.ActionType == ActionType.ClientHandled)
                                        {
                                            clientHandled.Add(tc);
                                        }
                                        else
                                        {
                                            // WebApi, LocalFunction, and SandboxHandled are all server-side
                                            serverHandled.Add(tc);
                                        }
                                    }
                                    else
                                    {
                                        // Unknown tool → treat as server-handled to preserve existing error behavior
                                        serverHandled.Add(tc);
                                    }
                                }

                                if (clientHandled.Count > 0)
                                {
                                    // Emit client-handled subset to the host/client and pause the run
                                    try
                                    {
                                        var json = JsonSerializer.Serialize(clientHandled);
                                        onExternalToolCall?.Invoke(null, new ExternalToolCallEventArgs(json));
                                    }
                                    catch { /* non-fatal */ }

                                    // Mark run results as pending client tool and end loop
                                    runResults = BuildRunResults(messages, response) ?? new ChatRunOutput { Messages = messages };
                                    runResults.Status = "pending_client_tool";
                                    continueChat = false;
                                }
                                else
                                {
                                    // No client tools → execute all tools as usual
                                    var (newFiles, modifiedFiles) = await DoToolCalls(
                                        assistantDef,
                                        serverHandled,
                                        messages,
                                        oAuthUserAccessToken: options.oAuthUserAccessToken,
                                        httpClient: httpClient,
                                        messageAdded: onMessage,
                                        ctx: ctx,
                                        cancellationToken: token);
                                    foreach (var f in newFiles) accumulatedNewFiles.Add(f);
                                    foreach (var f in modifiedFiles) accumulatedModifiedFiles.Add(f);
                                }
                            }
                            break;
                        case "length":
                            continueChat = false;
                            break;
                        case "function_call":
                            continueChat = false;
                            break;
                        default:
                            break;
                    }

                    runResults = BuildRunResults(messages, response);

                    if (choice.FinishReason == "stop" && !string.IsNullOrEmpty(options.Evaluator) && runResults != null)
                    {
                        while (evaluatorTurnCounter < 2)
                        {
                            var evaluatedPrompt = (runResults?.Dialog?.Replace(runResults.LastMessage, string.Empty) ?? "").Replace("User:", "MessageFromUser:").Replace("Assistant:", "MessageFromLLM:");

                            evaluatorTurnCounter++;
                            var evaluatorOptions = new ChatRunOptions()
                            {
                                AssistantName = options.Evaluator,
                                Instructions = $"[Input conversation]\n---\n{evaluatedPrompt}\n---\n[Assistant response for evaluation]\n---\n{runResults!.LastMessage}",
                                // Keep evaluator calls on the already-resolved deployment path so
                                // global chat override/default semantics are applied consistently.
                                DeploymentId = options.DeploymentId,
                                OverrideTemperature = options.OverrideTemperature,
                                OverrideTopP = options.OverrideTopP,
                                OverrideReasoningEffort = options.OverrideReasoningEffort
                            };

                            var evaluatorOutput = (await ExecuteAsync(evaluatorOptions, clientFactory, null, httpClient, null, null, ctx, token, isAgentInvocation: false))?.LastMessage ?? "";
                            if (!evaluatorOutput.Contains("End Conversation", StringComparison.OrdinalIgnoreCase))
                            {
                                messages.Add(new ChatMessage(ChatRole.User, evaluatorOutput));
                                continueChat = true;
                                break;
                            }
                        }
                    }
                }

                // Store accumulated files in the result for bubbling up to parent
                if (runResults != null)
                {
                    if (accumulatedNewFiles.Count > 0)
                        runResults.NewFiles = [.. accumulatedNewFiles];
                    if (accumulatedModifiedFiles.Count > 0)
                        runResults.ModifiedFiles = [.. accumulatedModifiedFiles];
                }

                return runResults;
            }
            catch (OperationCanceledException)
            {
                // Propagate cancellation so upstream callers can react appropriately
                throw;
            }
            catch (Exception ex)
            {
                throw new ChatConversationException(ex, runResults);
            }
        }

        private static async Task<ChatCompletionResponse> GetCompletionAndStreamAsync(
            IChatCompletionClient api,
            ChatCompletionRequest chatRequest,
            StreamingMessageProgressEventHandler streamingMessageProgress,
            CancellationToken cancellationToken)
        {
            if (streamingMessageProgress == null)
            {
                // should not happen but guard anyway
                return await api.GetCompletionAsync(chatRequest, cancellationToken);
            }

            var streamedContent = new StringBuilder();
            var hasStreamed = false;
            Exception? lastException = null;
            var attempt = 0;

            while (true)
            {
                try
                {
                    // Streaming handler returns deltas on the same thread
                    var finalResponse = await api.StreamCompletionAsync(chatRequest, partialResponse =>
                    {
                        var delta = partialResponse.FirstChoice?.Delta;
                        if (!string.IsNullOrEmpty(delta?.Content))
                        {
                            var finishReason = partialResponse.FirstChoice?.FinishReason;
                            var roleName = string.Equals(finishReason, "thinking", StringComparison.OrdinalIgnoreCase)
                                ? "assistant_thinking"
                                : delta?.Role?.ToString() ?? ChatRole.Assistant.ToString();
                            var normalized = NormalizeAssistantText(delta!.Content);
                            if (!string.IsNullOrEmpty(normalized))
                            {
                                streamedContent.Append(normalized);
                                hasStreamed = true;
                            }
                            streamingMessageProgress.Invoke(null, new StreamingMessageProgressEventArgs(roleName, normalized));
                        }
                    }, cancellationToken);

                    return finalResponse;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (IsTransientStreamFailure(ex, cancellationToken))
                {
                    lastException = ex;

                    if (hasStreamed)
                    {
                        var recovered = await TryRecoverStreamAsync(
                            api,
                            chatRequest,
                            streamedContent.ToString(),
                            streamingMessageProgress,
                            cancellationToken);
                        if (recovered != null)
                        {
                            return recovered;
                        }

                        break;
                    }

                    attempt++;
                    if (attempt >= StreamRetryMaxAttempts)
                    {
                        break;
                    }

                    var delay = GetStreamRetryDelay(attempt);
                    Trace.TraceWarning(
                        $"Streaming attempt {attempt} failed with {ex.GetType().Name}. Retrying in {delay.TotalMilliseconds}ms.");
                    await Task.Delay(delay, cancellationToken);
                }
            }

            throw lastException ?? new InvalidOperationException("Streaming failed without exception.");
        }

        private const int StreamRetryMaxAttempts = 2;
        private static readonly TimeSpan[] StreamRetryDelays =
        [
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10)
        ];

        private static TimeSpan GetStreamRetryDelay(int attempt)
        {
            var index = Math.Clamp(attempt - 1, 0, StreamRetryDelays.Length - 1);
            return StreamRetryDelays[index];
        }

        private static bool IsTransientStreamFailure(Exception ex, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (ex is OperationCanceledException)
            {
                return false;
            }

            if (ex is HttpRequestException httpEx)
            {
                if (httpEx.StatusCode == null)
                {
                    return true;
                }

                var statusCode = (int)httpEx.StatusCode.Value;
                return statusCode >= 500 || statusCode == 408 || statusCode == 429;
            }

            if (ex is IOException || ex is SocketException)
            {
                return true;
            }

            return ex.InnerException != null && IsTransientStreamFailure(ex.InnerException, cancellationToken);
        }

        private static async Task<ChatCompletionResponse?> TryRecoverStreamAsync(
            IChatCompletionClient api,
            ChatCompletionRequest chatRequest,
            string streamedContent,
            StreamingMessageProgressEventHandler streamingMessageProgress,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(streamedContent))
            {
                return null;
            }

            ChatCompletionResponse response;
            try
            {
                response = await api.GetCompletionAsync(chatRequest, cancellationToken);
            }
            catch (Exception ex) when (IsTransientStreamFailure(ex, cancellationToken))
            {
                Trace.TraceWarning($"Streaming recovery failed with {ex.GetType().Name}.");
                return null;
            }

            var fullText = NormalizeAssistantText(response.FirstChoice?.Message?.GetText());
            if (!fullText.StartsWith(streamedContent, StringComparison.Ordinal))
            {
                Trace.TraceWarning("Streaming recovery response did not match streamed prefix; aborting recovery.");
                return null;
            }

            var remaining = fullText[streamedContent.Length..];
            if (!string.IsNullOrEmpty(remaining))
            {
                streamingMessageProgress.Invoke(
                    null,
                    new StreamingMessageProgressEventArgs(ChatRole.Assistant.ToString(), remaining));
            }

            return response;
        }

        private static ChatRunOutput? BuildRunResults(List<ChatMessage> messages, ChatCompletionResponse response)
        {
            ChatRunOutput? runResults = new() { Messages = messages };

            var last = messages.Last();
            var lastText = last.GetText();
            if (last.Role == ChatRole.Assistant)
            {
                lastText = NormalizeAssistantText(lastText);
            }
            runResults.LastMessage = lastText;

            var choice = response.FirstChoice;
            runResults.Status = choice?.FinishReason ?? "unknown";

            foreach (var message in messages)
            {
                if (message.Role == ChatRole.System || message.Role == ChatRole.Developer) continue;

                string messageText = message.GetText();

                if (message.Role == ChatRole.User)
                {
                    runResults.ConversationMessages.Add(new() { Message = messageText, MessageType = ThreadConversationMessageType.User });
                }
                else if (message.Role == ChatRole.Assistant)
                {
                    var normalizedAssistant = NormalizeAssistantText(messageText);
                    runResults.ConversationMessages.Add(new() { Message = normalizedAssistant, MessageType = ThreadConversationMessageType.Assistant });
                }
                else if (message.Role == ChatRole.Tool)
                {
                    runResults.ConversationMessages.Add(new() { Message = messageText, MessageType = ThreadConversationMessageType.Tool });
                }
            }

            if (response.Usage != null)
            {
                runResults.Usage = new()
                {
                    CompletionTokens = response.Usage.CompletionTokens,
                    PromptTokens = response.Usage.PromptTokens ?? 0,
                    CachedPromptTokens = response.Usage.PromptTokensDetails?.CachedTokens ?? 0,
                    TotalTokens = response.Usage.TotalTokens ?? 0
                };
            }

            return runResults;
        }

        /// <summary>
        /// Executes tool calls and returns any files created/modified by the tools.
        /// </summary>
        /// <returns>Tuple of (NewFiles, ModifiedFiles) containing CWD-relative paths.</returns>
        public static async Task<(List<string> NewFiles, List<string> ModifiedFiles)> DoToolCalls(
            AssistantDefinition assistantDef,
            IReadOnlyList<ChatToolCall> toolCalls,
            List<ChatMessage> messages,
            string? oAuthUserAccessToken = null,
            HttpClient? httpClient = null,
            MessageAddedEventHandler? messageAdded = null,
            InvocationContext? ctx = null,
            CancellationToken cancellationToken = default)
        {
            var assistantName = assistantDef.Name!;

            if (ctx == null) throw new ArgumentNullException(nameof(ctx), "InvocationContext is required for tool calls");
            cancellationToken.ThrowIfCancellationRequested();

            await EnsureRequestBuilderCache(assistantName);

            var cacheKey = GenerateRequestBuilderCacheKey(assistantName);
            if (!RequestBuilderCache.TryGetValue(cacheKey, out var builders)) throw new Exception($"No request builders found for {assistantName}");

            var toolCallTasks = new List<Task<ToolOutput>>();

            foreach (var requiredOutput in toolCalls)
            {
                if (!requiredOutput.IsFunction) continue;
                var toolCallId = requiredOutput.Id;
                var toolName = requiredOutput.Function.Name;
                var parameters = requiredOutput.Function.Arguments;
                if (builders.TryGetValue(toolName, out ToolCaller? tool))
                {
                    var builder = tool.Clone();
                    if (builder.ActionType == ActionType.ClientHandled)
                    {
                        // Skip client-handled tools in server execution path
                        continue;
                    }

                    builder.Params = JsonSerializer.Deserialize<Dictionary<string, object>>(parameters.ToString());

                    // Fill in any missing required parameters using defaults from the schema
                    builder.AddMissingRequiredParamsFromSchema();

                    // Inject notebook/project context for known context tools OR for local crew-bridge operations
                    var requiresContext = RequiresNotebookContext(builder.Path)
                        || (builder.ActionType == ActionType.LocalFunction
                            && string.Equals(builder.Path, "AntRunner.Chat.Agent.Invoke", StringComparison.Ordinal));
                    if (requiresContext && ctx != null)
                    {
                        builder.Params ??= [];

                        // For Agent.Invoke, inject the InvocationContext with TriggeringToolCallId set
                        if (builder.ActionType == ActionType.LocalFunction
                            && string.Equals(builder.Path, "AntRunner.Chat.Agent.Invoke", StringComparison.Ordinal))
                        {
                            // Create a new context with the triggering tool call ID for invocation tracking
                            var nestedCtx = ctx with { TriggeringToolCallId = toolCallId, RunId = null };
                            builder.Params["context"] = nestedCtx;
                        }
                        else
                        {
                            // Inject isolated InvocationContext for parallel tool call safety
                            // Each tool call gets its own context copy so RunId mutations don't cross-contaminate
                            var isolatedCtx = ctx with { RunId = null };
                            builder.Params["context"] = isolatedCtx;
                        }

                        // For SearchAssistantFiles, also inject the AssistantDefinition
                        if (toolName == "SearchAssistantFiles")
                        {
                            builder.Params["assistantDefinition"] = assistantDef;
                        }
                    }

                    var task = Task.Run(async () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        string output;
                        if (builder.ActionType == ActionType.WebApi)
                        {
                            string responseContent;
                            try
                            {
                                var response = await builder.ExecuteWebApiAsync(
                                    oAuthUserAccessToken,
                                    httpClient ?? _httpClient,
                                    cancellationToken);
                                responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (AntRunner.ToolCalling.Functions.ToolCaller.MissingAssistantAuthException)
                            {
                                // Bubble up a friendly, actionable message and stop the call
                                return new ToolOutput
                                {
                                    Output = "This tool requires an API key for this host, but it isn't set. Open Guide Builder → Auth and provide the required value. Until then this API cannot be used.",
                                    ToolCallId = requiredOutput.Id
                                };
                            }
                            catch (HttpRequestException ex)
                            {
                                return new ToolOutput
                                {
                                    Output = $"Service unavailable. The API could not be reached: {ex.Message}",
                                    ToolCallId = requiredOutput.Id
                                };
                            }
                            catch (TaskCanceledException ex) when (ex.InnerException is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
                            {
                                return new ToolOutput
                                {
                                    Output = "Service unavailable. The request to the API timed out.",
                                    ToolCallId = requiredOutput.Id
                                };
                            }

                            if (builder.ResponseSchemas.TryGetValue("200", out var schemaJson))
                            {
                                try
                                {
                                    var contentJson = JsonDocument.Parse(responseContent).RootElement;

                                    var filteredJson = ChatRunnerUtils.FilterJsonBySchema(contentJson, schemaJson);
                                    output = filteredJson.GetRawText();
                                }
                                catch
                                {
                                    output = responseContent;
                                }
                            }
                            else
                            {
                                output = responseContent;
                            }
                        }
                        else if (builder.ActionType == ActionType.SandboxHandled)
                        {
                            // Execute sandbox tool via SandboxToolService
                            try
                            {
                                // ctx is validated non-null at method entry (line 583)
                                if (ctx == null)
                                {
                                    return new ToolOutput
                                    {
                                        Output = "ERROR: InvocationContext is required for sandbox tool execution.",
                                        ToolCallId = requiredOutput.Id
                                    };
                                }
                                
                                // Extract init script filename from URL: sandbox://init.py -> init.py
                                var initScriptFilename = ExtractSandboxInitFilename(builder.BaseUrl);
                                
                                // Function name is the operationId (matches builder.Operation)
                                var functionName = builder.Operation;
                                
                                // Inject context if not already present
                                builder.Params ??= [];
                                var isolatedCtx = ctx with { RunId = null };
                                builder.Params["context"] = isolatedCtx;
                                
                                // Execute the sandbox tool (calls static method that resolves service via DI)
                                var sandboxResult = await ExecuteSandboxToolStaticAsync(
                                    toolName,
                                    functionName,
                                    builder.Params,
                                    initScriptFilename,
                                    assistantDef.Name!,
                                    isolatedCtx);
                                
                                output = JsonSerializer.Serialize(sandboxResult);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                output = $"ERROR: Sandbox tool execution failed: {ex.Message}";
                            }
                        }
                        else
                        {
                            try
                            {
                                var toolResult = await builder.ExecuteLocalFunctionAsync(cancellationToken);
                                if (toolResult != null)
                                {
                                    output = JsonSerializer.Serialize(toolResult);
                                }
                                else
                                {
                                    output = "Operation completed successfully";
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                output = $"ERROR: {ex.Message}";
                            }
                        }

                        // Tool call usage: Only record here for agent invocations (CurrentInvocationId set).
                        // Regular conversation tool calls are recorded by ConversationService finalization.
                        try
                        {
                            if (ctx?.CurrentInvocationId != null)
                            {
                                var functionName = requiredOutput.Function!.Name;
                                var metadataJson = JsonSerializer.Serialize(new
                                {
                                    toolCallId = requiredOutput.Id,
                                    arguments = requiredOutput.Function.Arguments
                                });

                                // Record the tool call itself, attributed to the AgentInvocation.
                                ChatUsage.RecordToolCall(
                                    projectId: ctx.ProjectId,
                                    notebookId: ctx.NotebookId,
                                    conversationId: ctx.ConversationId,
                                    functionName: functionName,
                                    assistantId: assistantDef.Id,
                                    agentInvocationId: ctx.CurrentInvocationId,
                                    metadata: metadataJson);

                                // For image-generation tools, also record ImageGeneration usage so that
                                // the cost is attributed directly to the AgentInvocation rather than
                                // only to the outer notebook conversation message.
                                var isImageTool =
                                    string.Equals(functionName, "generate_image", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(functionName, "MakeImageFromImage", StringComparison.OrdinalIgnoreCase);

                                if (isImageTool)
                                {
                                    ChatUsage.RecordImageGeneration(
                                        projectId: ctx.ProjectId,
                                        notebookId: ctx.NotebookId,
                                        conversationId: ctx.ConversationId,
                                        imageCount: 1,
                                        bytes: 0,
                                        assistantId: assistantDef.Id,
                                        agentInvocationId: ctx.CurrentInvocationId,
                                        notebookConversationMessageId: null,
                                        metadata: metadataJson);
                                }
                            }
                        }
                        catch
                        {
                            // Non-fatal usage logging failure should never abort tool execution.
                        }

                        return new ToolOutput()
                        {
                            Output = output,
                            ToolCallId = requiredOutput.Id
                        };
                    }, cancellationToken);

                    toolCallTasks.Add(task);
                }
                else
                {
                    var task = Task.Run(() =>
                    {
                        Trace.TraceError($"No request builder found for {toolName}");
                        return new ToolOutput()
                        {
                            Output = $"Error: {toolName} is not a valid tool.",
                            ToolCallId = requiredOutput.Id
                        };
                    });
                    toolCallTasks.Add(task);
                }
            }

            // Collect all files from tool outputs (always, for bubbling up to parent)
            var allNewFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allModifiedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (toolCallTasks.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toolOutputs = await Task.WhenAll(toolCallTasks);

                foreach (var toolCall in toolCalls)
                {
                    if (!toolCall.IsFunction) continue;
                    var id = toolCall.Id;
                    var toolOutput = toolOutputs.FirstOrDefault(to => to.ToolCallId == id) ?? throw new Exception("No match");
                    messages.Add(new ChatMessage(id, toolCall.Function.Name, [new ChatContent(toolOutput.Output!)]));
                    messageAdded?.Invoke(null, new MessageAddedEventArgs(messages.Last().Role.ToString(), messages.Last().GetText(), toolCall.Id, toolCall.Function.Name, toolCall.Function.Arguments.ToString()));
                }

                // Extract file lists from all tool outputs
                foreach (var toolOutput in toolOutputs)
                {
                    if (string.IsNullOrEmpty(toolOutput.Output)) continue;

                    // Try to parse as ScriptExecutionResult to get file lists directly
                    try
                    {
                        var result = JsonSerializer.Deserialize<ScriptExecutionResult>(toolOutput.Output, ScriptExecutionResultJsonOptions);
                        if (result?.NewFiles != null)
                        {
                            foreach (var f in result.NewFiles) allNewFiles.Add(f);
                        }
                        if (result?.ModifiedFiles != null)
                        {
                            foreach (var f in result.ModifiedFiles) allModifiedFiles.Add(f);
                        }
                    }
                    catch { /* Not a ScriptExecutionResult, skip */ }
                }

                // Emit consolidated file-change system message (deduplicated per conversation)
                if (ctx != null && AssistantHasFilesContextOption(assistantDef))
                {
                    try
                    {
                        var set = ConversationFileAnnouncements.GetOrAdd(ctx.ConversationId, _ => new());
                        var freshNew = allNewFiles.Where(f => set.TryAdd(f, 0)).ToList();
                        var freshModified = allModifiedFiles.Where(f => set.TryAdd(f, 0)).ToList();

                        if (freshNew.Count > 0 || freshModified.Count > 0)
                        {
                            var payload = FormatFileChangesConsole(freshNew, freshModified);
                            messages.Add(new ChatMessage(ChatRole.System, payload));
                            messageAdded?.Invoke(null, new MessageAddedEventArgs("system", payload));
                        }
                    }
                    catch { /* non-fatal */ }
                }
            }

            return (allNewFiles.ToList(), allModifiedFiles.ToList());
        }

        static async Task EnsureRequestBuilderCache(string assistantName)
        {
            var cacheKey = GenerateRequestBuilderCacheKey(assistantName);
            if (RequestBuilderCache.ContainsKey(cacheKey))
            {
                return;
            }

            Dictionary<string, ToolCaller> assistantRequestBuilders = [];

            // Try to get OpenAPI schemas from database first
            var storageMetadata = await AssistantDefinitionFiles.GetAssistantComplete(assistantName);
            if (storageMetadata?.OpenApiSchemas != null && storageMetadata.OpenApiSchemas.Count > 0)
            {
                // Database has the schemas as Dictionary<filename, content>
                foreach (var kvp in storageMetadata.OpenApiSchemas)
                {
                    var json = kvp.Value;

                    var validationResult = OpenApiHelper.ValidateAndParseOpenApiSpec(json);
                    var spec = validationResult.Spec;

                    if (!validationResult.Status || spec == null)
                    {
                        TraceWarning($"OpenAPI schema '{kvp.Key}' from database is not valid. Ignoring");
                        continue;
                    }

                    var requestBuilders = storageMetadata.DomainAuth != null
                        ? ToolCaller.GetToolCallers(spec, storageMetadata.DomainAuth)
                        : await ToolCaller.GetToolCallers(spec, assistantName);

                    foreach (var tool in requestBuilders.Keys)
                    {
                        assistantRequestBuilders[tool] = requestBuilders[tool];
                    }
                }
            }
            else
            {
                // Fall back to static OpenAPI schema files from file system
                var openApiSchemaFiles = await AssistantDefinitionFiles.GetFilesInOpenApiFolder(assistantName);
                if (openApiSchemaFiles != null && openApiSchemaFiles.Count > 0)
                {
                    foreach (var openApiSchemaFile in openApiSchemaFiles)
                    {
                        var schema = await AssistantDefinitionFiles.GetFile(openApiSchemaFile);
                        if (schema == null)
                        {
                            TraceWarning($"openApiSchemaFile {openApiSchemaFile} is null. Ignoring");
                            continue;
                        }

                        var json = Encoding.Default.GetString(schema);

                        var validationResult = OpenApiHelper.ValidateAndParseOpenApiSpec(json);
                        var spec = validationResult.Spec;

                        if (!validationResult.Status || spec == null)
                        {
                            TraceWarning($"Json is not a valid OpenAPI spec {json}. Ignoring");
                            continue;
                        }

                        var requestBuilders = await ToolCaller.GetToolCallers(spec, assistantName);

                        foreach (var tool in requestBuilders.Keys)
                        {
                            assistantRequestBuilders[tool] = requestBuilders[tool];
                        }
                    }
                }
            }

            // Inject annotated tool builders dynamically based on assistant's tool list
            // This replaces all the hardcoded if blocks with a unified annotation-driven approach
            var assistantDef = await AssistantUtility.GetAssistantCreateRequest(assistantName);
            if (assistantDef?.Tools != null)
            {
                var allToolOperations = ToolContractRegistry.GetAllToolOperations();
                var assistantOperationIds = assistantDef.Tools
                    .Select(t => t.Function?.AsObject?.Name)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var (operationId, fullyQualifiedMethodName) in allToolOperations)
                {
                    if (assistantOperationIds.Contains(operationId))
                    {
                        try
                        {
                            var schema = ToolContractRegistry.GenerateOpenApiSchema(fullyQualifiedMethodName);
                            var validationResult = OpenApiHelper.ValidateAndParseOpenApiSpec(schema);

                            if (validationResult.Status && validationResult.Spec != null)
                            {
                                var requestBuilders = await ToolCaller.GetToolCallers(validationResult.Spec, assistantName);
                                foreach (var (toolName, builder) in requestBuilders)
                                {
                                    // Only add builders that appear in assistant's tool list
                                    if (assistantOperationIds.Contains(toolName))
                                    {
                                        assistantRequestBuilders[toolName] = builder;
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            TraceWarning($"Failed to generate schema for {fullyQualifiedMethodName}: {ex.Message}");
                        }
                    }
                }
            }


            // Inject crew-bridge tool builders for Guide assistants ONLY
            // These are only added when we have explicit __crew_names__ metadata from NotebookTemplate manifests
            if (assistantDef?.Metadata != null && assistantDef.Metadata.TryGetValue("__crew_names__", out var crewNamesStr))
            {
                var crewNames = crewNamesStr.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (crewNames.Count > 0)
                {
                    var crewAssistants = new List<AssistantDefinition>();
                    foreach (var crewName in crewNames)
                    {
                        var crewAssistant = await AssistantUtility.GetAssistantCreateRequest(crewName);
                        if (crewAssistant != null)
                        {
                            crewAssistants.Add(crewAssistant);
                        }
                    }

                    if (crewAssistants.Count > 0)
                    {
                        var bridgeSchema = CrewBridgeSchemaGenerator.GetSchema(crewAssistants);
                        var validationResult = OpenApiHelper.ValidateAndParseOpenApiSpec(bridgeSchema);
                        var spec = validationResult.Spec;

                        if (validationResult.Status && spec != null)
                        {
                            var bridgeRequestBuilders = await ToolCaller.GetToolCallers(spec, assistantName);
                            foreach (var tool in bridgeRequestBuilders.Keys)
                            {
                                // Add crew-bridge tools for this Guide assistant
                                assistantRequestBuilders[tool] = bridgeRequestBuilders[tool];
                            }
                        }
                    }
                }
            }

            RequestBuilderCache[cacheKey] = assistantRequestBuilders;
        }

        /// <summary>
        /// Determines if a tool requires notebook context parameters to be injected.
        /// Uses the ToolContractRegistry to check for RequiresNotebookContext attribute.
        /// </summary>
        /// <param name="path">The fully qualified method path</param>
        /// <returns>True if the tool requires notebook context parameters</returns>
        private static bool RequiresNotebookContext(string path)
        {
            // First check if we have a direct path match in the registry
            var contract = ToolContractRegistry.GetContract(path);
            return contract.RequiresNotebookContext;
        }

        private static readonly JsonSerializerOptions ScriptExecutionResultJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private static async Task<string?> ResolveReasoningEffortAsync(
            string? modelId,
            string? reasoningEffort,
            CancellationToken token)
        {
            return await DatabaseStorage.ResolveModelReasoningEffortAsync(modelId, reasoningEffort, token);
        }

        private static bool SupportsOpenAiReasoningEffortByModelId(string? modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
            {
                return false;
            }

            return modelId.StartsWith("o", StringComparison.OrdinalIgnoreCase)
                || modelId.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Extracts the initialization script filename from a sandbox:// URL.
        /// Example: "sandbox://init.py" -> "init.py"
        /// </summary>
        private static string ExtractSandboxInitFilename(string baseUrl)
        {
            // sandbox://init.py -> extract "init.py"
            var uri = new Uri(baseUrl);
            // The host portion contains the filename in sandbox:// URLs
            var filename = uri.Host;
            // If there's a path, append it (in case of sandbox://folder/init.py format)
            if (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
            {
                filename = uri.AbsolutePath.TrimStart('/');
            }
            return filename;
        }

        /// <summary>
        /// Executes a sandbox tool via the static SandboxToolService method.
        /// This is a bridge from the AntRunner.Chat library to GuideAntsApi services.
        /// </summary>
        private static async Task<ScriptExecutionResult> ExecuteSandboxToolStaticAsync(
            string toolName,
            string functionName,
            Dictionary<string, object> parameters,
            string initializationScriptFilename,
            string assistantName,
            InvocationContext context)
        {
            // Remove the injected context from parameters before passing to sandbox
            // (the sandbox service will use it internally, not pass to Python)
            var paramsForPython = new Dictionary<string, object>(parameters);
            paramsForPython.Remove("context");
            paramsForPython.Remove("assistantDefinition");

            // Use reflection to call the static method on SandboxToolService
            // This avoids a circular reference between AntRunner.Chat and GuideAntsApi
            var serviceType = Type.GetType("GuideAntsApi.Services.SandboxToolService, GuideAntsApi");
            if (serviceType == null)
            {
                return new ScriptExecutionResult
                {
                    StandardOutput = string.Empty,
                    StandardError = "ERROR: SandboxToolService not found. Ensure GuideAntsApi is properly referenced."
                };
            }

            var method = serviceType.GetMethod("ExecuteSandboxTool", 
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (method == null)
            {
                return new ScriptExecutionResult
                {
                    StandardOutput = string.Empty,
                    StandardError = "ERROR: ExecuteSandboxTool method not found on SandboxToolService."
                };
            }

            try
            {
                if (method.Invoke(null, [toolName, functionName, paramsForPython, initializationScriptFilename, assistantName, context])
                    is not Task<ScriptExecutionResult> task)
                {
                    return new ScriptExecutionResult
                    {
                        StandardOutput = string.Empty,
                        StandardError = "ERROR: ExecuteSandboxTool did not return expected Task type."
                    };
                }

                return await task;
            }
            catch (Exception ex)
            {
                return new ScriptExecutionResult
                {
                    StandardOutput = string.Empty,
                    StandardError = $"ERROR: Failed to invoke sandbox tool: {ex.InnerException?.Message ?? ex.Message}"
                };
            }
        }

    }
}
