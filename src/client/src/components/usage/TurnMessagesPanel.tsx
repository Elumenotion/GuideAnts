import { useEffect, useState } from 'react';
import { API_BASE_URL } from '../../config/apiConfig';
import { guideUsageApi } from '../../services/guideUsageApi';
import { broadcastAuthExpired } from '../../services/authEvents';
import { withAuthFetchInit, withAuthHeaders } from '../../services/authService';
import type { TurnMessagesDto, TurnMessageDto } from '../../types/usage';

interface TurnMessagesPanelProps {
  conversationId: string | null;
  turnIndex: number | null;
  onClose: () => void;
  onInvocationClick: (invocationId: string) => void;
}

type TurnMessageViewTab = 'messages' | 'promptTrace';

interface TurnPromptTraceMessageDto {
  role: string;
  content: string | null;
  toolCallId: string | null;
  functionName: string | null;
  toolCallsJson: string | null;
}

interface TurnPromptTraceToolDefinitionDto {
  name: string;
  description: string | null;
  parametersJson: string | null;
  source: string;
}

interface TurnPromptTraceToolCallDto {
  id: string;
  name: string;
  argumentsJson: string | null;
}

interface TurnPromptTraceRoundDto {
  roundIndex: number;
  createdUtc: string;
  modelDeploymentId: string | null;
  responseFinishReason: string | null;
  responseMessage: TurnPromptTraceMessageDto | null;
  requestMessages: TurnPromptTraceMessageDto[];
  externalToolCalls: TurnPromptTraceToolCallDto[];
}

interface TurnPromptTraceMessageEventDto {
  createdUtc: string;
  role: string;
  content: string | null;
  toolCallId: string | null;
  functionName: string | null;
  toolCallsJson: string | null;
}

interface TurnPromptTraceSegmentDto {
  segmentId: string;
  status: string;
  startedUtc: string;
  completedUtc: string | null;
  assistantName: string;
  modelDeploymentId: string | null;
  terminalStatus: string | null;
  errorMessage: string | null;
  seedMessages: TurnPromptTraceMessageDto[];
  toolDefinitions: TurnPromptTraceToolDefinitionDto[];
  rounds: TurnPromptTraceRoundDto[];
  messageEvents: TurnPromptTraceMessageEventDto[];
}

interface TurnPromptTraceDto {
  conversationId: string;
  turnIndex: number;
  assistantName: string | null;
  turnStarted: string;
  hasTrace: boolean;
  schemaVersion: number;
  captureState: string;
  segments: TurnPromptTraceSegmentDto[];
}

export function TurnMessagesPanel({
  conversationId,
  turnIndex,
  onClose,
  onInvocationClick,
}: TurnMessagesPanelProps) {
  const [data, setData] = useState<TurnMessagesDto | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [activeTab, setActiveTab] = useState<TurnMessageViewTab>('messages');
  const [traceData, setTraceData] = useState<TurnPromptTraceDto | null>(null);
  const [traceLoading, setTraceLoading] = useState(false);
  const [traceError, setTraceError] = useState<string | null>(null);

  const isOpen = conversationId !== null && turnIndex !== null;

  useEffect(() => {
    setActiveTab('messages');
    setTraceData(null);
    setTraceLoading(false);
    setTraceError(null);
  }, [conversationId, turnIndex]);

  useEffect(() => {
    if (!conversationId || turnIndex === null) {
      setData(null);
      return;
    }

    let cancelled = false;

    async function load() {
      setLoading(true);
      setError(null);
      try {
        const result = await guideUsageApi.getTurnMessages(conversationId!, turnIndex!);
        if (!cancelled) {
          setData(result);
        }
      } catch (e: any) {
        if (!cancelled) {
          setError(e.message || 'Failed to load turn messages');
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    }

    load();
    return () => {
      cancelled = true;
    };
  }, [conversationId, turnIndex]);

  useEffect(() => {
    if (!conversationId || turnIndex === null || activeTab !== 'promptTrace' || traceData) {
      return;
    }

    let cancelled = false;
    const controller = new AbortController();

    async function loadPromptTrace() {
      setTraceLoading(true);
      setTraceError(null);
      try {
        const response = await fetch(
          `${API_BASE_URL}/conversations/${conversationId}/turns/${turnIndex}/trace`,
          withAuthFetchInit({
            method: 'GET',
            headers: withAuthHeaders({
              'Content-Type': 'application/json',
            }),
            signal: controller.signal,
          }),
        );

        if (response.status === 401) {
          broadcastAuthExpired('Authentication expired.');
        }

        if (!response.ok) {
          throw new Error(`Failed to load prompt trace: ${response.status}`);
        }

        const result = (await response.json()) as TurnPromptTraceDto;
        if (!cancelled) {
          setTraceData(result);
        }
      } catch (e: any) {
        if (!cancelled && e?.name !== 'AbortError') {
          setTraceError(e?.message ?? 'Failed to load prompt trace');
        }
      } finally {
        if (!cancelled) {
          setTraceLoading(false);
        }
      }
    }

    loadPromptTrace();
    return () => {
      cancelled = true;
      controller.abort();
    };
  }, [activeTab, conversationId, turnIndex, traceData]);

  const getRoleBadgeClass = (role: string) => {
    switch (role.toLowerCase()) {
      case 'user':
        return 'bg-blue-100 text-blue-800';
      case 'assistant':
        return 'bg-green-100 text-green-800';
      case 'system':
        return 'bg-gray-100 text-gray-800';
      case 'tool':
        return 'bg-purple-100 text-purple-800';
      default:
        return 'bg-gray-100 text-gray-600';
    }
  };

  const formatJson = (raw: string | null | undefined) => {
    if (!raw) {
      return '';
    }

    try {
      return JSON.stringify(JSON.parse(raw), null, 2);
    } catch {
      return raw;
    }
  };

  const renderMessage = (msg: TurnMessageDto) => {
    const hasInvocation = msg.agentInvocationId !== null;

    return (
      <div key={msg.id} className="border rounded-lg p-3 bg-gray-50">
        <div className="flex items-center justify-between mb-2">
          <div className="flex items-center gap-2">
            <span
              className={`px-2 py-0.5 text-xs font-medium rounded ${getRoleBadgeClass(msg.role)}`}
            >
              {msg.role}
              {msg.functionName && ` (${msg.functionName})`}
            </span>
            {hasInvocation && (
              <button
                onClick={() => onInvocationClick(msg.agentInvocationId!)}
                className="px-2 py-0.5 text-xs font-medium rounded bg-indigo-100 text-indigo-700 hover:bg-indigo-200 transition-colors"
                title="View agent invocation details"
              >
                → View Invocation
              </button>
            )}
          </div>
          <span className="text-xs text-gray-500">
            {new Date(msg.created).toLocaleTimeString()}
          </span>
        </div>
        <div className="text-sm text-gray-900 whitespace-pre-wrap break-words max-h-48 overflow-y-auto">
          {msg.content || <span className="text-gray-400 italic">No content</span>}
        </div>
        {msg.toolCallsJson && (
          <div className="mt-2 p-2 bg-purple-50 rounded text-xs font-mono overflow-x-auto">
            <div className="text-purple-700 font-medium mb-1">Tool Calls:</div>
            <pre className="text-purple-600 whitespace-pre-wrap">
              {formatJson(msg.toolCallsJson)}
            </pre>
          </div>
        )}
      </div>
    );
  };

  const renderTraceMessage = (message: TurnPromptTraceMessageDto, key: string) => (
    <div key={key} className="border rounded-lg p-3 bg-gray-50">
      <div className="flex items-center gap-2 mb-2">
        <span className={`px-2 py-0.5 text-xs font-medium rounded ${getRoleBadgeClass(message.role)}`}>
          {message.role}
          {message.functionName && ` (${message.functionName})`}
        </span>
        {message.toolCallId && (
          <span className="text-xs text-gray-500">tool_call_id: {message.toolCallId}</span>
        )}
      </div>
      <div className="text-sm text-gray-900 whitespace-pre-wrap break-words max-h-40 overflow-y-auto">
        {message.content || <span className="text-gray-400 italic">No content</span>}
      </div>
      {message.toolCallsJson && (
        <div className="mt-2 p-2 bg-purple-50 rounded text-xs font-mono overflow-x-auto">
          <div className="text-purple-700 font-medium mb-1">Tool Calls:</div>
          <pre className="text-purple-600 whitespace-pre-wrap">
            {formatJson(message.toolCallsJson)}
          </pre>
        </div>
      )}
    </div>
  );

  const renderPromptTrace = () => {
    if (traceLoading) {
      return (
        <div className="flex items-center justify-center py-12">
          <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-600" />
        </div>
      );
    }

    if (traceError) {
      return (
        <div className="bg-red-50 border border-red-200 rounded-lg p-4 text-red-700">
          {traceError}
        </div>
      );
    }

    if (!traceData) {
      return (
        <div className="text-sm text-gray-500 text-center py-8">
          Prompt trace is unavailable.
        </div>
      );
    }

    if (!traceData.hasTrace) {
      return (
        <div className="text-sm text-gray-500 text-center py-8">
          No prompt trace captured for this turn.
        </div>
      );
    }

    return (
      <div className="space-y-4">
        {traceData.segments.map((segment, segmentIndex) => (
          <div key={segment.segmentId} className="border rounded-lg p-4 bg-gray-50 space-y-4">
            <div className="flex items-center justify-between gap-3">
              <div className="text-sm font-semibold text-gray-900">
                Segment {segmentIndex + 1}
              </div>
              <div className="text-xs text-gray-600 flex gap-3">
                <span>Status: <span className="font-medium">{segment.status}</span></span>
                <span>Started: <span className="font-medium">{new Date(segment.startedUtc).toLocaleTimeString()}</span></span>
                {segment.completedUtc && (
                  <span>Completed: <span className="font-medium">{new Date(segment.completedUtc).toLocaleTimeString()}</span></span>
                )}
              </div>
            </div>

            {segment.errorMessage && (
              <div className="bg-red-50 border border-red-200 rounded p-3 text-sm text-red-700">
                {segment.errorMessage}
              </div>
            )}

            <details className="border rounded bg-white">
              <summary className="cursor-pointer px-3 py-2 text-sm font-medium text-gray-800">
                Seed Messages ({segment.seedMessages.length})
              </summary>
              <div className="p-3 space-y-3">
                {segment.seedMessages.map((message, idx) => renderTraceMessage(message, `seed-${segment.segmentId}-${idx}`))}
              </div>
            </details>

            <details className="border rounded bg-white">
              <summary className="cursor-pointer px-3 py-2 text-sm font-medium text-gray-800">
                Tool Definitions ({segment.toolDefinitions.length})
              </summary>
              <div className="p-3 space-y-3">
                {segment.toolDefinitions.length === 0 && (
                  <div className="text-sm text-gray-500">No tools captured.</div>
                )}
                {segment.toolDefinitions.map((tool, idx) => (
                  <div key={`tool-${segment.segmentId}-${idx}`} className="border rounded p-3 bg-gray-50">
                    <div className="flex items-center justify-between">
                      <div className="text-sm font-medium text-gray-900">{tool.name}</div>
                      <span className="text-xs px-2 py-0.5 rounded bg-indigo-100 text-indigo-700">{tool.source}</span>
                    </div>
                    {tool.description && (
                      <div className="mt-1 text-sm text-gray-700">{tool.description}</div>
                    )}
                    {tool.parametersJson && (
                      <pre className="mt-2 p-2 bg-indigo-50 rounded text-xs font-mono whitespace-pre-wrap overflow-x-auto text-indigo-700">
                        {formatJson(tool.parametersJson)}
                      </pre>
                    )}
                  </div>
                ))}
              </div>
            </details>

            <details className="border rounded bg-white" open>
              <summary className="cursor-pointer px-3 py-2 text-sm font-medium text-gray-800">
                Model Rounds ({segment.rounds.length})
              </summary>
              <div className="p-3 space-y-4">
                {segment.rounds.length === 0 && (
                  <div className="text-sm text-gray-500">No model rounds captured.</div>
                )}
                {segment.rounds.map((round) => (
                  <div key={`round-${segment.segmentId}-${round.roundIndex}`} className="border rounded p-3 bg-gray-50 space-y-3">
                    <div className="text-sm font-medium text-gray-900">
                      Round {round.roundIndex}
                      {round.modelDeploymentId ? ` • ${round.modelDeploymentId}` : ''}
                      {round.responseFinishReason ? ` • finish: ${round.responseFinishReason}` : ''}
                    </div>

                    <div>
                      <div className="text-xs font-medium uppercase tracking-wide text-gray-500 mb-2">Request Messages</div>
                      <div className="space-y-2">
                        {round.requestMessages.map((message, idx) =>
                          renderTraceMessage(message, `request-${segment.segmentId}-${round.roundIndex}-${idx}`),
                        )}
                      </div>
                    </div>

                    {round.responseMessage && (
                      <div>
                        <div className="text-xs font-medium uppercase tracking-wide text-gray-500 mb-2">Response Message</div>
                        {renderTraceMessage(round.responseMessage, `response-${segment.segmentId}-${round.roundIndex}`)}
                      </div>
                    )}

                    {round.externalToolCalls.length > 0 && (
                      <div>
                        <div className="text-xs font-medium uppercase tracking-wide text-gray-500 mb-2">
                          External Tool Calls ({round.externalToolCalls.length})
                        </div>
                        <div className="space-y-2">
                          {round.externalToolCalls.map((toolCall) => (
                            <div key={toolCall.id} className="border rounded p-2 bg-white">
                              <div className="text-sm font-medium text-gray-900">{toolCall.name}</div>
                              {toolCall.argumentsJson && (
                                <pre className="mt-1 text-xs font-mono bg-purple-50 rounded p-2 whitespace-pre-wrap overflow-x-auto text-purple-700">
                                  {formatJson(toolCall.argumentsJson)}
                                </pre>
                              )}
                            </div>
                          ))}
                        </div>
                      </div>
                    )}
                  </div>
                ))}
              </div>
            </details>

            <details className="border rounded bg-white">
              <summary className="cursor-pointer px-3 py-2 text-sm font-medium text-gray-800">
                Message Events ({segment.messageEvents.length})
              </summary>
              <div className="p-3 space-y-2">
                {segment.messageEvents.length === 0 && (
                  <div className="text-sm text-gray-500">No message events captured.</div>
                )}
                {segment.messageEvents.map((event, idx) => (
                  <div key={`event-${segment.segmentId}-${idx}`} className="border rounded p-2 bg-gray-50">
                    <div className="flex items-center gap-2 mb-1">
                      <span className={`px-2 py-0.5 text-xs font-medium rounded ${getRoleBadgeClass(event.role)}`}>
                        {event.role}
                      </span>
                      <span className="text-xs text-gray-500">{new Date(event.createdUtc).toLocaleTimeString()}</span>
                    </div>
                    <div className="text-sm text-gray-900 whitespace-pre-wrap break-words">
                      {event.content || <span className="text-gray-400 italic">No content</span>}
                    </div>
                  </div>
                ))}
              </div>
            </details>
          </div>
        ))}
      </div>
    );
  };

  return (
    <>
      {/* Backdrop */}
      {isOpen && (
        <div
          className="fixed inset-0 bg-black bg-opacity-25 z-40"
          onClick={onClose}
        />
      )}

      {/* Panel */}
      <div
        className="fixed inset-0 z-50 flex items-center justify-center p-4 pointer-events-none"
        style={{ display: isOpen ? 'flex' : 'none' }}
      >
        <div
          className={`bg-white rounded-lg shadow-xl max-w-4xl w-full max-h-[90vh] overflow-hidden flex flex-col pointer-events-auto transform transition-all ${
            isOpen ? 'scale-100 opacity-100' : 'scale-95 opacity-0'
          }`}
        >
          {/* Header */}
          <div className="flex items-center justify-between p-4 md:p-6 border-b flex-shrink-0">
            <div>
              <h2 className="text-lg font-semibold">Turn Messages</h2>
              {activeTab === 'messages' && data && (
                <div className="mt-1 text-sm text-gray-600">
                  <span>Turn {turnIndex! + 1}</span>
                  {data.assistantName && (
                    <>
                      <span className="text-gray-400 mx-2">•</span>
                      <span>{data.assistantName}</span>
                    </>
                  )}
                  <span className="text-gray-400 mx-2">•</span>
                  <span>{data.messages.length} messages</span>
                </div>
              )}
              {activeTab === 'promptTrace' && traceData && (
                <div className="mt-1 text-sm text-gray-600">
                  <span>Turn {turnIndex! + 1}</span>
                  <span className="text-gray-400 mx-2">•</span>
                  <span>Trace state: {traceData.captureState}</span>
                  <span className="text-gray-400 mx-2">•</span>
                  <span>{traceData.segments.length} segments</span>
                </div>
              )}
            </div>
            <button
              onClick={onClose}
              className="p-1 hover:bg-gray-100 rounded"
              aria-label="Close"
            >
              <svg
                className="w-5 h-5 text-gray-500"
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeWidth={2}
                  d="M6 18L18 6M6 6l12 12"
                />
              </svg>
            </button>
          </div>

          {/* Tabs */}
          <div className="px-4 md:px-6 py-2 border-b bg-gray-50 flex gap-2">
            <button
              onClick={() => setActiveTab('messages')}
              className={`px-3 py-1.5 text-sm rounded border transition-colors ${
                activeTab === 'messages'
                  ? 'bg-white border-blue-300 text-blue-700'
                  : 'bg-white border-gray-200 text-gray-600 hover:text-gray-800'
              }`}
            >
              Messages
            </button>
            <button
              onClick={() => setActiveTab('promptTrace')}
              className={`px-3 py-1.5 text-sm rounded border transition-colors ${
                activeTab === 'promptTrace'
                  ? 'bg-white border-blue-300 text-blue-700'
                  : 'bg-white border-gray-200 text-gray-600 hover:text-gray-800'
              }`}
            >
              Prompt Trace
            </button>
          </div>

          {/* Content */}
          <div className="flex-1 overflow-y-auto p-4 md:p-6">
            {activeTab === 'messages' && (
              <>
                {loading && (
                  <div className="flex items-center justify-center py-12">
                    <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-600" />
                  </div>
                )}

                {error && (
                  <div className="bg-red-50 border border-red-200 rounded-lg p-4 text-red-700">
                    {error}
                  </div>
                )}

                {data && !loading && (
                  <div className="space-y-3">
                    {data.messages.map(renderMessage)}
                  </div>
                )}

                {data && data.messages.length === 0 && !loading && (
                  <div className="text-sm text-gray-500 text-center py-8">
                    No messages in this turn.
                  </div>
                )}
              </>
            )}

            {activeTab === 'promptTrace' && renderPromptTrace()}
          </div>
        </div>
      </div>
    </>
  );
}




