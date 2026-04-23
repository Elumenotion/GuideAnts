import React from 'react';
import { useConversation } from '../../../contexts/ConversationContext';
import ConversationHeader from './ConversationHeader';
import CellList from './CellList';

interface ConversationPanelProps {
  conversationId: string;
  projectId: string;
  notebookId: string;
  canEdit?: boolean;
  collaboratorCount?: number;
  onNewConversation?: (conversationId: string) => void;
  isRuntimeLoading?: boolean;
}

/**
 * ConversationPanel presents a cell-based chat UI for a single conversation.
 * Implements the new cell-based layout with inline editing and draft composition.
 */
export const ConversationPanel: React.FC<ConversationPanelProps> = ({ 
  conversationId: _conversationId,
  canEdit = false,
  onNewConversation,
  isRuntimeLoading = false
}) => {
  return (
    <div className="flex flex-col h-full min-h-0 w-full overflow-hidden relative">
      <ConversationPanelContent canEdit={canEdit} onNewConversation={onNewConversation} isRuntimeLoading={isRuntimeLoading} />
    </div>
  );
};

const ConversationPanelContent: React.FC<{ canEdit: boolean; onNewConversation?: (conversationId: string) => void; isRuntimeLoading?: boolean }> = ({ canEdit, onNewConversation, isRuntimeLoading }) => {
  const { 
    messages, 
    isStreaming, 
    streamingMode,
    sendMessage, 
    undoLastTurn,
    draftUserContent,
    setDraftUserContent,
    editingAssistantId,
    startEditingAssistant,
    editAssistantMessage,
    selectedAssistant,
    setSelectedAssistant,
    assistants,
    conversationStarters,
    editError,
    isEditLoading,
    streamingError,
    onPreviewFile,
    onPreviewFileByPath
  } = useConversation();

  return (
    <div className="flex flex-col h-full min-h-0 w-full overflow-hidden">
      {/* Conversation header */}
      <ConversationHeader onUndo={undoLastTurn} canEdit={canEdit} onNewConversation={onNewConversation} data-tour-id="conversation.header" />

      {streamingError && (
        <div className="mx-4 mt-3 rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700" data-tour-id="conversation.error">
          {streamingError}
        </div>
      )}

      {/* Main conversation content */}
      <CellList
        data-tour-id="conversation.messages"
        messages={messages}
        isStreaming={isStreaming || !!isRuntimeLoading}
        streamingMode={streamingMode}
        draftUserContent={draftUserContent}
        editingAssistantId={editingAssistantId}
        selectedAssistant={selectedAssistant || undefined}
        assistants={assistants}
        conversationStarters={conversationStarters}
        canEdit={canEdit && !isRuntimeLoading}

        onDraftChange={canEdit && !isRuntimeLoading ? setDraftUserContent : () => {}}
        onSendMessage={canEdit && !isRuntimeLoading ? sendMessage : () => {}}
        onEditAssistant={canEdit && !isRuntimeLoading ? startEditingAssistant : () => {}}
        onSaveAssistant={canEdit && !isRuntimeLoading ? editAssistantMessage : async () => {}}
        onAssistantSelect={canEdit && !isRuntimeLoading ? setSelectedAssistant : () => {}}
        onUndo={canEdit && !isRuntimeLoading ? undoLastTurn : () => {}}
        onEditUserMessage={(messageId, content) => {
          if (!canEdit || isRuntimeLoading) return;
          console.log('Edit user message:', messageId, content);
          sendMessage(content);
        }}
        editError={editError}
        isEditLoading={isEditLoading}
        turnBasedMode={true}
        onPreviewFile={onPreviewFile}
        onPreviewFileByPath={onPreviewFileByPath}
      />
    </div>
  );
}; 



