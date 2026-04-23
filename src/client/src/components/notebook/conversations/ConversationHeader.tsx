import { useCallback } from 'react';
import { useConversation } from '../../../contexts/ConversationContext';
import { useNotebook } from '../../../contexts/NotebookContext';
import AssistantSelector, { AssistantOption } from './AssistantSelector';
import { HiPlus } from 'react-icons/hi';

interface ConversationHeaderProps {
  onUndo: () => void;
  className?: string;
  canEdit?: boolean;
  'data-tour-id'?: string;
  onNewConversation?: (conversationId: string) => void;
}

export default function ConversationHeader({ onUndo: _onUndo, className = '', canEdit = false, 'data-tour-id': dataTourId, onNewConversation }: ConversationHeaderProps) {
  const {
    selectedAssistant,
    setSelectedAssistant,
    assistants,
    isStreaming,
    isCancelling,
    cancelStream,
  } = useConversation();
  
  const { createConversation } = useNotebook();

  const current = selectedAssistant ?? assistants[0]?.name ?? '';

  // Handle creating new conversation - only used on mobile
  const handleNewConversation = useCallback(async () => {
    if (!canEdit) return;
    try {
      const newConvo = await createConversation('New Conversation');
      if (newConvo) {
        // Trigger sidebar to refresh conversations immediately
        window.dispatchEvent(new Event('refresh-conversations'));
        if (onNewConversation) {
          onNewConversation(newConvo.id);
        }
      }
    } catch (error) {
      console.error('Failed to create conversation:', error);
    }
  }, [createConversation, canEdit, onNewConversation]);

  return (
    <div className={`border-b bg-white ${className}`} data-tour-id={dataTourId}>
      <div className="w-full flex items-center justify-between px-2 py-1.5 sm:px-3 sm:py-2 md:px-4">
        <div className="flex items-center gap-1 sm:gap-2">
          {/* Stop button */}
          {isStreaming && (
            <button
              onClick={cancelStream}
              disabled={isCancelling}
              className={`flex items-center gap-1 sm:gap-2 px-2 sm:px-3 py-1 sm:py-1.5 rounded-md text-xs sm:text-sm font-medium transition-colors ${
                isCancelling
                  ? 'bg-gray-100 text-gray-400 cursor-not-allowed'
                  : 'bg-red-500 hover:bg-red-600 text-white'
              }`}
              title={isCancelling ? 'Stopping...' : 'Stop generation'}
            >
              <div className={`w-3 h-3 ${isCancelling ? 'animate-spin' : ''}`}>
                {isCancelling ? (
                  <svg className="w-3 h-3" viewBox="0 0 24 24">
                    <circle
                      className="opacity-25"
                      cx="12"
                      cy="12"
                      r="10"
                      stroke="currentColor"
                      strokeWidth="4"
                      fill="none"
                    />
                    <path
                      className="opacity-75"
                      fill="currentColor"
                      d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"
                    />
                  </svg>
                ) : (
                  <svg className="w-3 h-3" viewBox="0 0 24 24" fill="currentColor">
                    <rect x="6" y="4" width="4" height="16" rx="1" />
                    <rect x="14" y="4" width="4" height="16" rx="1" />
                  </svg>
                )}
              </div>
              {isCancelling ? 'Stopping...' : 'Stop'}
            </button>
          )}

          {/* New conversation button - mobile only, hidden while streaming */}
          {canEdit && !isStreaming && (
            <button
              onClick={handleNewConversation}
              className="md:hidden p-1.5 text-white bg-blue-600 hover:bg-blue-700 rounded-full flex items-center justify-center focus:outline-none focus:ring-2 focus:ring-blue-500 transition-colors"
              title="New conversation"
              aria-label="New conversation"
            >
              <HiPlus className="h-4 w-4" />
            </button>
          )}
        </div>
        
        <div className="flex items-center gap-1.5 sm:gap-2 md:gap-4">
          {/* Assistant selector */}
          <div data-tour-id="conversation.assistant.selector">
            <AssistantSelector
              assistants={assistants as AssistantOption[]}
              selectedName={current}
              onSelect={canEdit ? setSelectedAssistant : () => {}}
              disabled={!canEdit}
              data-tour-id="conversation.assistant.selector.dropdown"
            />
          </div>
        </div>
      </div>
    </div>
  );
} 