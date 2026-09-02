import ChatMarkdownViewer from './ChatMarkdownViewer';
import UserAvatar from '../../common/UserAvatar';
import { ConfirmationDialog } from '../../common/ConfirmationDialog';
import { FaUndoAlt, FaExpandAlt } from 'react-icons/fa';
import { useState } from 'react';
import { AttachedFile } from '../../../types/conversation';

interface UserCellProps {
  content: string;
  isLast: boolean;
  onUndo?: () => void;
  userId?: string;
  userName?: string;
  userEmail?: string;
  attachments?: AttachedFile[];
  onPreviewFile?: (fileId: string) => void;
  onPreviewFileByPath?: (relativePath: string) => void;
  projectId?: string;
  notebookId?: string;
}

/**
 * UserCell displays a read-only user message in cell-based layout.
 * Shows undo button on the last user cell if provided.
 * Displays user avatar with initials instead of generic "U".
 */
export default function UserCell({
  content,
  isLast,
  onUndo,
  userId,
  userName,
  userEmail,
  attachments,
  onPreviewFile,
  onPreviewFileByPath,
  projectId,
  notebookId,
}: UserCellProps) {
  const [isFull, setIsFull] = useState(false);
  const [showUndoConfirm, setShowUndoConfirm] = useState(false);

  const handleUndoClick = () => {
    setShowUndoConfirm(true);
  };

  const handleUndoConfirm = () => {
    setShowUndoConfirm(false);
    if (onUndo) {
      onUndo();
    }
  };

  const handleUndoCancel = () => {
    setShowUndoConfirm(false);
  };

  if (isFull) {
    return (
      <div className="fixed inset-0 z-50 bg-white overflow-auto select-text">
        {/* Header with controls */}
        <div className="absolute top-4 right-4 flex items-center space-x-2 z-10">
          <button
            onClick={() => setIsFull(false)}
            className="p-1 hover:bg-gray-100 rounded text-gray-500 hover:text-gray-700"
            aria-label="Exit full screen"
            title="Exit full screen"
          >
            <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>

        {/* Content area */}
        <div className="pt-6 pr-16 pb-4 pl-4">
          <ChatMarkdownViewer
            text={content}
            className="prose prose-sm max-w-none"
            projectId={projectId}
            notebookId={notebookId}
          />
        </div>
      </div>
    );
  }

  return (
    <>
      {/* User avatar in first column */}
      <div className="flex justify-center items-start pt-2 sm:pt-3 md:pt-4">
        <UserAvatar
          userId={userId}
          userName={userName}
          userEmail={userEmail}
          size="sm" // 32px on mobile
          className="sm:!h-10 sm:!w-10 md:!h-12 md:!w-12 lg:!h-14 lg:!w-14" // Restore desktop sizes
        />
      </div>

      {/* Message content spanning into the third column */}
      <div
        className={`col-span-2 relative border rounded-md bg-white p-2 sm:p-3 md:p-4 shadow-sm my-1 sm:my-2 w-full pl-8 sm:pl-10 md:pl-6 lg:pl-6 ${isLast ? 'pr-8 sm:pr-10 md:pr-12 lg:pr-16' : ''}`}
        role="group"
        aria-label="User message"
        data-testid="message"
        data-role="user"
      >
        {/* Content */}
        <div className="min-h-[2rem] mt-2 mb-2">
          <ChatMarkdownViewer
            text={content}
            className="prose prose-sm max-w-none"
            maxImageHeight={450}
            projectId={projectId}
            notebookId={notebookId}
          />
        </div>

        {attachments && attachments.length > 0 && (
          <div className="flex flex-wrap gap-2 mt-2">
            {attachments.map((att) => (
              <button
                key={att.id}
                onClick={() => {
                  if (att.relativePath && onPreviewFileByPath) {
                    onPreviewFileByPath(att.relativePath);
                  } else if (att.notebookFileId) {
                    onPreviewFile?.(att.notebookFileId);
                  }
                }}
                className="flex items-center px-2 py-1 bg-gray-100 hover:bg-gray-200 rounded text-xs cursor-pointer transition-colors"
                title={`Click to preview ${att.fileName}`}
              >
                <span className="mr-1">📎</span>
                <span className="truncate max-w-[120px]" title={att.fileName}>
                  {att.fileName}
                </span>
                {att.fileSize > 0 && (
                  <span className="ml-1 text-gray-500">
                    ({(att.fileSize / 1024).toFixed(0)}KB)
                  </span>
                )}
              </button>
            ))}
          </div>
        )}

        {/* Action buttons container - only if last message */}
        {isLast && (
          <div className="absolute top-2 right-2 flex flex-col items-center space-y-1">
            <button
              onClick={() => setIsFull(true)}
              className="p-1 hover:bg-gray-100 rounded text-gray-500 hover:text-gray-700"
              aria-label="Full screen"
              title="Full screen"
            >
              <FaExpandAlt className="w-4 h-4" />
            </button>

            {onUndo && (
              <button
                onClick={handleUndoClick}
                className="p-1 hover:bg-gray-100 rounded text-gray-500 hover:text-gray-700"
                aria-label="Undo last turn"
                title="Undo last turn"
              >
                <FaUndoAlt className="w-4 h-4" />
              </button>
            )}
          </div>
        )}
      </div>

      {/* Undo confirmation dialog */}
      <ConfirmationDialog
        isOpen={showUndoConfirm}
        onClose={handleUndoCancel}
        onConfirm={handleUndoConfirm}
        title="Undo Last Turn"
        message="This will remove your last message and the assistant's response. This action cannot be undone."
        confirmText="Undo"
        cancelText="Cancel"
        confirmButtonClass="bg-red-600 hover:bg-red-700 text-white"
      />
    </>
  );
}
