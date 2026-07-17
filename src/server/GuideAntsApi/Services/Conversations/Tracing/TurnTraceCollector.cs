using System.Text.Json;
using AntRunner.Chat;

namespace GuideAntsApi.Services.Conversations.Tracing;

public sealed class TurnTraceCollector : IThreadRunTraceCollector
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _sync = new();
    private readonly TurnTraceSegment _segment;

    public TurnTraceCollector(string assistantName, string? modelDeploymentId)
    {
        _segment = new TurnTraceSegment
        {
            SegmentId = Guid.NewGuid(),
            StartedUtc = DateTime.UtcNow,
            AssistantName = assistantName,
            ModelDeploymentId = modelDeploymentId
        };
    }

    public void CaptureSeedMessages(IReadOnlyList<ThreadRunTraceMessageSnapshot> messages)
    {
        lock (_sync)
        {
            _segment.SeedMessages = messages.Select(ToTraceMessage).ToList();
        }
    }

    public void CaptureToolDefinitions(IReadOnlyList<ThreadRunTraceToolDefinitionSnapshot> tools)
    {
        lock (_sync)
        {
            _segment.ToolDefinitions = tools.Select(t => new TurnTraceToolDefinition
            {
                Name = t.Name,
                Description = t.Description,
                ParametersJson = t.ParametersJson,
                Source = t.Source
            }).ToList();
        }
    }

    public void CaptureRoundRequest(
        int roundIndex,
        string? modelDeploymentId,
        IReadOnlyList<ThreadRunTraceMessageSnapshot> requestMessages,
        IReadOnlyList<ThreadRunTraceToolDefinitionSnapshot> tools)
    {
        lock (_sync)
        {
            var existing = _segment.Rounds.FirstOrDefault(r => r.RoundIndex == roundIndex);
            var round = existing ?? new TurnTraceRound
            {
                RoundIndex = roundIndex,
                CreatedUtc = DateTime.UtcNow
            };

            round.ModelDeploymentId = modelDeploymentId;
            round.RequestMessages = requestMessages.Select(ToTraceMessage).ToList();

            if (existing == null)
            {
                _segment.Rounds.Add(round);
            }

            if (_segment.ToolDefinitions.Count == 0 && tools.Count > 0)
            {
                _segment.ToolDefinitions = tools.Select(t => new TurnTraceToolDefinition
                {
                    Name = t.Name,
                    Description = t.Description,
                    ParametersJson = t.ParametersJson,
                    Source = t.Source
                }).ToList();
            }
        }
    }

    public void CaptureRoundResponse(
        int roundIndex,
        string? finishReason,
        ThreadRunTraceMessageSnapshot responseMessage)
    {
        lock (_sync)
        {
            var round = _segment.Rounds.FirstOrDefault(r => r.RoundIndex == roundIndex);
            if (round == null)
            {
                round = new TurnTraceRound
                {
                    RoundIndex = roundIndex,
                    CreatedUtc = DateTime.UtcNow
                };
                _segment.Rounds.Add(round);
            }

            round.ResponseFinishReason = finishReason;
            round.ResponseMessage = ToTraceMessage(responseMessage);
        }
    }

    public void CaptureExternalToolCalls(
        int roundIndex,
        IReadOnlyList<ThreadRunTraceToolCallSnapshot> toolCalls)
    {
        lock (_sync)
        {
            var round = _segment.Rounds.FirstOrDefault(r => r.RoundIndex == roundIndex);
            if (round == null)
            {
                round = new TurnTraceRound
                {
                    RoundIndex = roundIndex,
                    CreatedUtc = DateTime.UtcNow
                };
                _segment.Rounds.Add(round);
            }

            round.ExternalToolCalls = toolCalls.Select(t => new TurnTraceToolCall
            {
                Id = t.Id,
                Name = t.Name,
                ArgumentsJson = t.ArgumentsJson
            }).ToList();
        }
    }

    public void CaptureMessageEvent(
        string role,
        string? content,
        string? toolCallId,
        string? functionName,
        string? toolCallsJson)
    {
        lock (_sync)
        {
            _segment.MessageEvents.Add(new TurnTraceMessageEvent
            {
                CreatedUtc = DateTime.UtcNow,
                Role = NormalizeRole(role),
                Content = content,
                ToolCallId = toolCallId,
                FunctionName = functionName,
                ToolCallsJson = toolCallsJson
            });
        }
    }

    public void CaptureTerminalStatus(string status, string? errorMessage = null)
    {
        lock (_sync)
        {
            _segment.TerminalStatus = status;
            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                _segment.ErrorMessage = errorMessage;
            }
        }
    }

    public void CaptureToolLimitState(
        int toolCallsUsed,
        string escalationPhase)
    {
        lock (_sync)
        {
            _segment.ToolLimitCallsUsed = toolCallsUsed;
            _segment.ToolLimitEscalationPhase = escalationPhase;
        }
    }

    public TurnTraceSegment BuildFinalizedSegment(string captureState, string? errorMessage = null)
    {
        lock (_sync)
        {
            _segment.Status = captureState;
            _segment.CompletedUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                _segment.ErrorMessage = errorMessage;
            }

            var json = JsonSerializer.Serialize(_segment, JsonOptions);
            return JsonSerializer.Deserialize<TurnTraceSegment>(json, JsonOptions)
                ?? throw new InvalidOperationException("Failed to clone turn trace segment.");
        }
    }

    private static TurnTraceMessage ToTraceMessage(ThreadRunTraceMessageSnapshot message) => new()
    {
        Role = NormalizeRole(message.Role),
        Content = message.Content,
        ToolCallId = message.ToolCallId,
        FunctionName = message.FunctionName,
        ToolCallsJson = message.ToolCallsJson
    };

    private static string NormalizeRole(string? role) =>
        string.IsNullOrWhiteSpace(role) ? "unknown" : role.Trim().ToLowerInvariant();
}
