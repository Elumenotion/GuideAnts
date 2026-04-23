import { useEffect, useState } from 'react';
import { guideUsageApi } from '../../services/guideUsageApi';
import type { TurnMessagesDto, TurnMessageDto } from '../../types/usage';

interface TurnMessagesPanelProps {
  conversationId: string | null;
  turnIndex: number | null;
  onClose: () => void;
  onInvocationClick: (invocationId: string) => void;
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

  const isOpen = conversationId !== null && turnIndex !== null;

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
              {(() => {
                try {
                  return JSON.stringify(JSON.parse(msg.toolCallsJson), null, 2);
                } catch {
                  return msg.toolCallsJson;
                }
              })()}
            </pre>
          </div>
        )}
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
              {data && (
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
          <div className="flex-1 overflow-y-auto p-4 md:p-6">
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
          </div>
        </div>
      </div>
    </>
  );
}




