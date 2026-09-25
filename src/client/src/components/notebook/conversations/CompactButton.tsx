import { useState } from 'react';
import { api } from '../../../services/api';
import { useToast } from '../../common/Toast';
import type { ConversationCompactionResult } from '../../../types/notebook';

interface CompactButtonProps {
  projectId: string;
  notebookId: string;
  conversationId: string;
  disabled?: boolean;
  onSuccess?: () => void;
  onCompacted?: (result: ConversationCompactionResult) => void;
}

export default function CompactButton({ projectId, notebookId, conversationId, disabled = false, onSuccess, onCompacted }: CompactButtonProps) {
  const [isCompacting, setIsCompacting] = useState(false);
  const { showToast } = useToast();

  const handleClick = async () => {
    if (disabled || isCompacting) return;
    setIsCompacting(true);
    try {
      const result = await api.projects.notebooks.conversations.compact(projectId, notebookId, conversationId);
      const isNoOp = result.estimatedTokensBefore == null || result.messagesSummarized === 0;

      if (isNoOp) {
        showToast({
          type: 'info',
          title: 'Nothing to compact yet',
          message: 'Send at least one message first.',
          duration: 6000,
        });
      } else {
        showToast({
          type: 'success',
          title: 'Conversation compacted',
          message: `Summarized ${result.messagesSummarized} earlier message(s). The next message you send will use the smaller history.`,
          duration: 6000,
        });
        onSuccess?.();
        onCompacted?.(result);
      }
    } catch (err: any) {
      if (err?.status === 409) {
        showToast({
          type: 'warning',
          title: 'Conversation is busy',
          message: 'Wait for the current response to finish and try again.',
          duration: 6000,
        });
      } else {
        showToast({
          type: 'error',
          title: 'Failed to compact conversation',
          message: err?.message || 'An unexpected error occurred.',
          duration: 6000,
        });
      }
    } finally {
      setIsCompacting(false);
    }
  };

  return (
    <button
      type="button"
      onClick={handleClick}
      disabled={disabled || isCompacting}
      className="px-2 py-1.5 text-xs text-gray-600 rounded hover:bg-gray-100 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
      title="Summarize older messages to free up context"
    >
      {isCompacting ? 'Compacting…' : 'Compact'}
    </button>
  );
}
