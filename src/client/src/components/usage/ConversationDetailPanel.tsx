import { useEffect, useState } from 'react';
import { guideUsageApi } from '../../services/guideUsageApi';
import type { ConversationUsageSummaryDto, TurnInvocationTreeDto } from '../../types/usage';
import { InvocationTree } from './InvocationTree';
import { InvocationDetailPanel } from './InvocationDetailPanel';
import { TurnMessagesPanel } from './TurnMessagesPanel';

interface ConversationDetailPanelProps {
  conversation: ConversationUsageSummaryDto | null;
  allowedAssistantIds: string[];
  onClose: () => void;
}

export function ConversationDetailPanel({
  conversation,
  allowedAssistantIds,
  onClose,
}: ConversationDetailPanelProps) {
  const [turns, setTurns] = useState<TurnInvocationTreeDto[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [selectedInvocationId, setSelectedInvocationId] = useState<string | null>(null);
  const [selectedTurnForMessages, setSelectedTurnForMessages] = useState<number | null>(null);

  const isOpen = conversation !== null;

  useEffect(() => {
    if (!conversation) {
      setTurns([]);
      return;
    }

    let cancelled = false;

    async function load(current: ConversationUsageSummaryDto) {
      setLoading(true);
      setError(null);
      try {
        // Single API call to get all turns at once
        const results = await guideUsageApi.getAllTurnInvocations(current.conversationId);

        if (!cancelled) {
          setTurns(results);
        }
      } catch (e: any) {
        if (!cancelled) {
          setError(e.message || 'Failed to load conversation invocations');
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    }

    load(conversation);
    return () => {
      cancelled = true;
    };
  }, [conversation]);

  const formatNumber = (value: number) =>
    new Intl.NumberFormat('en-US').format(value);

  const formatCurrency = (value: number) =>
    new Intl.NumberFormat('en-US', {
      style: 'currency',
      currency: 'USD',
      minimumFractionDigits: 2,
      maximumFractionDigits: 4,
    }).format(value);

  const formatDateTime = (iso: string) =>
    new Date(iso).toLocaleString(undefined, {
      month: 'short',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
    });

  const totalTokens = (conversation?.promptTokens ?? 0)
    + (conversation?.cachedTokens ?? 0)
    + (conversation?.reasoningTokens ?? 0)
    + (conversation?.completionTokens ?? 0);

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
          className={`bg-white rounded-lg shadow-xl max-w-5xl w-full max-h-[90vh] overflow-hidden flex flex-col pointer-events-auto transform transition-all ${
            isOpen ? 'scale-100 opacity-100' : 'scale-95 opacity-0'
          }`}
        >
          {/* Header */}
          <div className="flex items-center justify-between p-4 md:p-6 border-b flex-shrink-0">
            <div>
              <h2 className="text-lg font-semibold">
                Conversation Details
              </h2>
              {conversation && (
                <div className="mt-1 text-sm text-gray-600">
                  <div className="truncate">
                    {conversation.title || '(Untitled conversation)'}
                  </div>
                  <div className="flex items-center gap-3 mt-0.5">
                    <span>{formatDateTime(conversation.created)}</span>
                    <span className="text-gray-400">•</span>
                    <span>{conversation.assistantSteps} assistant steps</span>
                    <span className="text-gray-400">•</span>
                    <span>{conversation.totalToolCalls} tool calls</span>
                  </div>
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

          {/* Content */}
          <div className="flex-1 overflow-y-auto p-4 md:p-6 space-y-6">
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

            {conversation && !loading && !error && (
              <>
                {/* Summary cards */}
                <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
                  <div className="bg-gray-50 rounded-lg p-3">
                    <div className="text-xs text-gray-500">Input Tokens</div>
                    <div className="text-lg font-semibold">
                      {formatNumber(conversation.promptTokens
                        + conversation.cachedTokens
                        + conversation.reasoningTokens)}
                    </div>
                  </div>
                  <div className="bg-gray-50 rounded-lg p-3">
                    <div className="text-xs text-gray-500">Output Tokens</div>
                    <div className="text-lg font-semibold">
                      {formatNumber(conversation.completionTokens)}
                    </div>
                  </div>
                  <div className="bg-gray-50 rounded-lg p-3">
                    <div className="text-xs text-gray-500">Total Tokens</div>
                    <div className="text-lg font-semibold">
                      {formatNumber(totalTokens)}
                    </div>
                  </div>
                  <div className="bg-gray-50 rounded-lg p-3">
                    <div className="text-xs text-gray-500">Total Cost</div>
                    <div className="text-lg font-semibold">
                      {formatCurrency(conversation.totalCost)}
                    </div>
                  </div>
                </div>

                {/* Turn invocation trees */}
                {turns.length > 0 && (
                  <div className="space-y-6">
                    {turns.map((turn, idx) => {
                      const filteredRoots =
                        allowedAssistantIds && allowedAssistantIds.length > 0
                          ? turn.rootInvocations.filter(
                              (node) =>
                                !node.assistantId ||
                                allowedAssistantIds.includes(node.assistantId)
                            )
                          : turn.rootInvocations;

                      return (
                        <div
                          key={turn.turnIndex}
                          className="border rounded-lg p-4 bg-gray-50"
                        >
                          <div className="flex items-center justify-between mb-3">
                            <div className="flex items-center gap-3">
                              <div className="text-sm font-semibold text-gray-900">
                                Turn {idx + 1}
                              </div>
                              <button
                                onClick={() => setSelectedTurnForMessages(turn.turnIndex)}
                                className="px-2 py-1 text-xs font-medium rounded bg-blue-100 text-blue-700 hover:bg-blue-200 transition-colors"
                                title="View conversation messages"
                              >
                                View Messages
                              </button>
                            </div>
                            <div className="text-xs text-gray-500 flex gap-3">
                              <span>
                                Tokens:{' '}
                                <span className="font-medium">
                                  {formatNumber(turn.totalTokens)}
                                </span>
                              </span>
                              <span>
                                Cost:{' '}
                                <span className="font-medium">
                                  {formatCurrency(turn.totalChargeUsd)}
                                </span>
                              </span>
                            </div>
                          </div>
                          {/* In the conversation-level usage view, only show usage-based nodes
                              (agents + tool / AI service UsageEvents). Message-level traces are
                              available in the per-invocation detail panel. */}
                          <InvocationTree
                            nodes={filteredRoots}
                            onNodeClick={(node) => {
                              // Only drill into real AgentInvocation nodes (those with AssistantId).
                              if (node.assistantId) {
                                setSelectedInvocationId(node.id);
                              }
                            }}
                          />
                        </div>
                      );
                    })}
                  </div>
                )}

                {turns.length === 0 && (
                  <div className="text-sm text-gray-500">
                    No invocation data recorded for this conversation.
                  </div>
                )}
              </>
            )}
          </div>
        </div>
      </div>
      {selectedInvocationId && (
        <InvocationDetailPanel
          invocationId={selectedInvocationId}
          onClose={() => setSelectedInvocationId(null)}
        />
      )}
      {selectedTurnForMessages !== null && conversation && (
        <TurnMessagesPanel
          conversationId={conversation.conversationId}
          turnIndex={selectedTurnForMessages}
          onClose={() => setSelectedTurnForMessages(null)}
          onInvocationClick={(invocationId) => {
            setSelectedTurnForMessages(null);
            setSelectedInvocationId(invocationId);
          }}
        />
      )}
    </>
  );
}


