import { useEffect, useState } from 'react';
import { guideUsageApi } from '../../services/guideUsageApi';
import type { InvocationNodeDto, InvocationMessageDto } from '../../types/usage';
import { InvocationTree } from './InvocationTree';

interface InvocationDetailPanelProps {
  invocationId: string | null;
  onClose: () => void;
}

export function InvocationDetailPanel({ 
  invocationId, 
  onClose 
}: InvocationDetailPanelProps) {
  const [invocation, setInvocation] = useState<InvocationNodeDto | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Selection is not currently used for the tree view, but we keep the hook
  // structure for potential future enhancements.

  useEffect(() => {
    if (!invocationId) {
      setInvocation(null);
      return;
    }

    let cancelled = false;
    
    async function load() {
      setLoading(true);
      setError(null);
      try {
        const data = await guideUsageApi.getInvocation(invocationId!, true);
        if (!cancelled) {
          setInvocation(data);
        }
      } catch (e: any) {
        if (!cancelled) {
          setError(e.message || 'Failed to load invocation');
        }
      } finally {
        if (!cancelled) {
          setLoading(false);
        }
      }
    }

    load();
    return () => { cancelled = true; };
  }, [invocationId]);

  const formatNumber = (value: number) =>
    new Intl.NumberFormat('en-US').format(value);

  const formatCurrency = (value: number) =>
    new Intl.NumberFormat('en-US', { 
      style: 'currency', 
      currency: 'USD', 
      minimumFractionDigits: 2,
      maximumFractionDigits: 4
    }).format(value);

  const getRoleBadgeClass = (role: string) => {
    switch (role.toLowerCase()) {
      case 'user': return 'bg-blue-100 text-blue-800';
      case 'assistant': return 'bg-green-100 text-green-800';
      case 'system': return 'bg-gray-100 text-gray-800';
      case 'tool': return 'bg-purple-100 text-purple-800';
      default: return 'bg-gray-100 text-gray-600';
    }
  };

  const renderMessage = (msg: InvocationMessageDto) => (
    <div key={msg.id} className="border rounded-lg p-3 bg-gray-50">
      <div className="flex items-center justify-between mb-2">
        <span className={`px-2 py-0.5 text-xs font-medium rounded ${getRoleBadgeClass(msg.role)}`}>
          {msg.role}
          {msg.functionName && ` (${msg.functionName})`}
        </span>
        <span className="text-xs text-gray-500">
          {new Date(msg.created).toLocaleTimeString()}
        </span>
      </div>
      <div className="text-sm text-gray-900 whitespace-pre-wrap break-words max-h-48 overflow-y-auto">
        {msg.content || (
          <span className="text-gray-400 italic">No content</span>
        )}
      </div>
      {msg.toolCallsJson && (
        <div className="mt-2 p-2 bg-purple-50 rounded text-xs font-mono overflow-x-auto">
          <div className="text-purple-700 font-medium mb-1">Tool Calls:</div>
          <pre className="text-purple-600 whitespace-pre-wrap">
            {JSON.stringify(JSON.parse(msg.toolCallsJson), null, 2)}
          </pre>
        </div>
      )}
    </div>
  );

  const isOpen = invocationId !== null;

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
        className={`fixed inset-0 z-50 flex items-center justify-center p-4 pointer-events-none`}
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
              <h2 className="text-lg font-semibold">
                {invocation?.assistantName || 'Invocation'} Details
              </h2>
              {invocation && (
                <div className="flex items-center gap-3 mt-1 text-sm text-gray-600">
                  <span className={`px-2 py-0.5 rounded text-xs font-medium ${
                    invocation.status === 'completed' ? 'bg-green-100 text-green-700' :
                    invocation.status === 'failed' ? 'bg-red-100 text-red-700' :
                    invocation.status === 'running' ? 'bg-blue-100 text-blue-700' :
                    'bg-gray-100 text-gray-700'
                  }`}>
                    {invocation.status}
                  </span>
                </div>
              )}
            </div>
            <button 
              onClick={onClose} 
              className="p-1 hover:bg-gray-100 rounded"
              aria-label="Close"
            >
              <svg className="w-5 h-5 text-gray-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          </div>

          {/* Content */}
          <div className="flex-1 overflow-y-auto p-4 md:p-6">
            {loading && (
              <div className="flex items-center justify-center py-12">
                <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-600"></div>
              </div>
            )}

            {error && (
              <div className="bg-red-50 border border-red-200 rounded-lg p-4 text-red-700">
                {error}
              </div>
            )}

            {invocation && !loading && (
              <div className="space-y-6">
                {/* Stats Grid */}
                <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
                  <div className="bg-gray-50 rounded-lg p-3">
                    <div className="text-xs text-gray-500">Prompt Tokens</div>
                    <div className="text-lg font-semibold">{formatNumber(invocation.promptTokens)}</div>
                  </div>
                  <div className="bg-gray-50 rounded-lg p-3">
                    <div className="text-xs text-gray-500">Completion Tokens</div>
                    <div className="text-lg font-semibold">{formatNumber(invocation.completionTokens)}</div>
                  </div>
                  <div className="bg-gray-50 rounded-lg p-3">
                    <div className="text-xs text-gray-500">Cost</div>
                    <div className="text-lg font-semibold">{formatCurrency(invocation.chargeUsd)}</div>
                  </div>
                  <div className="bg-gray-50 rounded-lg p-3">
                    <div className="text-xs text-gray-500">Tool Calls</div>
                    <div className="text-lg font-semibold">{invocation.toolCallCount}</div>
                  </div>
                </div>

                {/* Additional Info */}
                <div className="grid grid-cols-2 gap-4 text-sm">
                  <div>
                    <span className="text-gray-500">LLM Round-trips:</span>
                    <span className="ml-2 font-medium">{invocation.llmRoundTrips}</span>
                  </div>
                  <div>
                    <span className="text-gray-500">Depth:</span>
                    <span className="ml-2 font-medium">{invocation.depth}</span>
                  </div>
                  {invocation.modelDeploymentId && (
                    <div className="col-span-2">
                      <span className="text-gray-500">Model:</span>
                      <span className="ml-2 font-mono text-xs">{invocation.modelDeploymentId}</span>
                    </div>
                  )}
                </div>

                {/* Invocation Tree (root + children including tool calls and message-level tool traces) */}
                {invocation && (
                  <div className="border-t pt-4">
                    <div className="text-sm font-medium text-gray-900 mb-2">
                      Invocation Tree
                    </div>
                    <InvocationTree nodes={[invocation]} showMessages />
                  </div>
                )}

                {/* Messages */}
                {invocation.messages && invocation.messages.length > 0 && (
                  <div className="border-t pt-4">
                    <div className="text-sm font-medium text-gray-900 mb-3">
                      Messages ({invocation.messages.length})
                    </div>
                    <div className="space-y-3 max-h-[400px] overflow-y-auto">
                      {invocation.messages.map(renderMessage)}
                    </div>
                  </div>
                )}
              </div>
            )}
          </div>
        </div>
      </div>
    </>
  );
}

