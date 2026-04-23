import type { InvocationNodeDto } from '../../types/usage';

interface InvocationTreeProps {
  nodes: InvocationNodeDto[];
  level?: number;
  /**
   * When true, renders a compact view of tool-related messages
   * (Tool role or toolCallsJson/functionName) under each node.
   * Used by the Conversation Details panel to show the tree more fully.
   */
  showMessages?: boolean;
  /**
   * Optional click handler for nodes (e.g., to open an invocation detail panel).
   */
  onNodeClick?: (node: InvocationNodeDto) => void;
}

export function InvocationTree({ nodes, level = 0, showMessages = false, onNodeClick }: InvocationTreeProps) {
  if (!nodes || nodes.length === 0) {
    return null;
  }

  const indentClass =
    level === 0
      ? ''
      : 'pl-4 border-l border-gray-200 ml-2';

  const formatNumber = (value: number) =>
    new Intl.NumberFormat('en-US').format(value);

  const formatCurrency = (value: number) =>
    new Intl.NumberFormat('en-US', {
      style: 'currency',
      currency: 'USD',
      minimumFractionDigits: 2,
      maximumFractionDigits: 4,
    }).format(value);

  return (
    <ul className={`space-y-1 ${indentClass}`}>
      {nodes.map((node) => {
        const totalTokens = (node.promptTokens ?? 0) + (node.completionTokens ?? 0);
        const isToolNode = node.assistantName.startsWith('Tool: ');
        // AI Service nodes are created with AssistantId = null and a label equal to
        // the UsageEvent.Operation (e.g., "image-generation").
        const isServiceNode = !isToolNode && !node.assistantId;
        const toolMessages = showMessages && node.messages
          ? node.messages.filter((m) => {
              const role = m.role.toLowerCase();
              return (
                role === 'tool' ||
                !!m.functionName ||
                (m.toolCallsJson !== null && m.toolCallsJson !== '')
              );
            })
          : [];

        return (
          <li key={node.id}>
            <div
              className={`flex items-center justify-between text-xs md:text-sm py-1 ${
                onNodeClick && !isToolNode && !isServiceNode
                  ? 'cursor-pointer hover:bg-gray-100 rounded px-1 -mx-1'
                  : ''
              }`}
              onClick={
                onNodeClick && !isToolNode && !isServiceNode
                  ? () => onNodeClick(node)
                  : undefined
              }
            >
              <div className="flex items-center gap-2">
                {isToolNode && (
                  <span className="inline-flex items-center px-1.5 py-0.5 rounded bg-purple-50 text-purple-700 text-[10px] font-semibold">
                    TOOL
                  </span>
                )}
                {!isToolNode && isServiceNode && (
                  <span className="inline-flex items-center px-1.5 py-0.5 rounded bg-emerald-50 text-emerald-700 text-[10px] font-semibold">
                    AI SERVICE
                  </span>
                )}
                {!isToolNode && !isServiceNode && (
                  <span className="inline-flex items-center px-1.5 py-0.5 rounded bg-blue-50 text-blue-700 text-[10px] font-semibold">
                    AGENT
                  </span>
                )}
                <span className="font-medium text-gray-900 truncate max-w-[200px] md:max-w-xs">
                  {node.assistantName}
                </span>
                {node.status && node.status !== 'completed' && (
                  <span className="px-1.5 py-0.5 rounded text-[10px] font-medium bg-gray-100 text-gray-600">
                    {node.status}
                  </span>
                )}
              </div>
              <div className="flex items-center gap-3 text-[10px] md:text-xs text-gray-500">
                {totalTokens > 0 && (
                  <span>
                    Tokens:{' '}
                    <span className="font-medium text-gray-800">
                      {formatNumber(totalTokens)}
                    </span>
                  </span>
                )}
                {node.chargeUsd > 0 && (
                  <span>
                    Cost:{' '}
                    <span className="font-medium text-gray-800">
                      {formatCurrency(node.chargeUsd)}
                    </span>
                  </span>
                )}
              </div>
            </div>
            {showMessages && toolMessages.length > 0 && (
              <ul className="ml-6 mt-1 space-y-0.5 border-l border-dashed border-purple-200 pl-2">
                {toolMessages.map((msg) => {
                  const role = msg.role.toLowerCase();
                  const label =
                    msg.functionName && msg.functionName.length > 0
                      ? `Tool: ${msg.functionName}`
                      : role === 'tool'
                        ? 'Tool result'
                        : 'Tool call';
                  const snippet = msg.content
                    ? msg.content.replace(/\s+/g, ' ').slice(0, 120)
                    : '';

                  return (
                    <li key={msg.id} className="flex items-start gap-1 text-[10px] text-gray-600">
                      <span className="mt-0.5 inline-flex items-center px-1 py-0.5 rounded bg-purple-50 text-purple-700 font-semibold">
                        TOOL
                      </span>
                      <span className="truncate max-w-xs md:max-w-sm">
                        <span className="font-medium text-gray-800">{label}</span>
                        {snippet && <span className="ml-1 text-gray-500">– {snippet}…</span>}
                      </span>
                    </li>
                  );
                })}
              </ul>
            )}
            {node.children && node.children.length > 0 && (
              <InvocationTree
                nodes={node.children}
                level={level + 1}
                showMessages={showMessages}
                onNodeClick={onNodeClick}
              />
            )}
          </li>
        );
      })}
    </ul>
  );
}


