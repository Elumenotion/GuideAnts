import ChatMarkdownViewer from './ChatMarkdownViewer';
import FullScreenEditor from './FullScreenEditor';
import { FaExpandAlt, FaEdit, FaTimes, FaCompress, FaCopy, FaSave, FaPlus, FaPen } from 'react-icons/fa';
import { useState, useCallback, useEffect, useMemo } from 'react';
import { createPortal } from 'react-dom';
import UserAvatar from '../../common/UserAvatar';
import MessageDiff from './MessageDiff';
import { useToast } from '../../common/Toast';
import { SaveAssistantContentDialog } from '../dialogs/SaveAssistantContentDialog';
import { generateMarkdownFile, AssistantContentMetadata } from '../../../utils/assistantContentFile';
import { resolveAgainstApiBase } from '../../../config/apiConfig';
import { FileIcon } from '../../common/FileIcon';
import { getContentTypeFromFileName } from '../../../utils/fileUtils';
import { useLongPress } from '../../../hooks/useLongPress';
import { decodeAssistantUnicodeEscapes } from '../../../utils/assistantUnicodeEscapes';

// File pill component for turn files (extracted for proper hook usage)
interface FilePillProps {
  filePath: string;
  type: 'created' | 'modified';
  isMobile: boolean;
  onPreview: (path: string) => void;
}

function FilePill({ filePath, type, isMobile, onPreview }: FilePillProps) {
  const fileName = filePath.split('/').pop() || filePath;
  const isCreated = type === 'created';

  // Long press on mobile to select in sidebar (if sidebar is visible)
  const longPressHandlers = useLongPress({
    onLongPress: () => {
      // Long press: select in sidebar
      window.dispatchEvent(new CustomEvent('select-notebook-file', { detail: { relativePath: filePath } }));
    },
    disabled: !isMobile,
    threshold: 500,
  });

  const handleClick = (e: React.MouseEvent) => {
    e.stopPropagation();
    if (isMobile) {
      // Mobile: single tap opens preview
      onPreview(filePath);
    } else {
      // Desktop: single click selects in sidebar
      window.dispatchEvent(new CustomEvent('select-notebook-file', { detail: { relativePath: filePath } }));
    }
  };

  const handleDoubleClick = (e: React.MouseEvent) => {
    e.stopPropagation();
    if (!isMobile) {
      // Desktop: double click opens preview
      onPreview(filePath);
    }
  };

  return (
    <button
      type="button"
      onClick={handleClick}
      onDoubleClick={handleDoubleClick}
      className={`inline-flex items-center gap-1 px-2 py-0.5 text-xs font-medium rounded-full 
        ${isCreated 
          ? 'bg-emerald-50 text-emerald-700 border border-emerald-200 hover:bg-emerald-100 hover:border-emerald-300' 
          : 'bg-amber-50 text-amber-700 border border-amber-200 hover:bg-amber-100 hover:border-amber-300'
        } transition-colors cursor-pointer`}
      title={isMobile 
        ? `${isCreated ? 'New' : 'Modified'}: ${filePath} (tap to preview, hold to select in sidebar)` 
        : `${isCreated ? 'New' : 'Modified'}: ${filePath} (click to select, double-click to preview)`
      }
      {...longPressHandlers}
    >
      {isCreated ? <FaPlus className="w-2.5 h-2.5" /> : <FaPen className="w-2.5 h-2.5" />}
      <FileIcon 
        contentType={getContentTypeFromFileName(fileName)} 
        fileName={fileName} 
        className="w-3 h-3" 
      />
      <span className="max-w-[120px] truncate">{fileName}</span>
    </button>
  );
}

interface AssistantCellProps {
  content: string;
  isLast: boolean;
  isStreaming?: boolean;
  /** Indicates the message has been edited by user. */
  isEdited?: boolean;
  /** Original (pre-edit) assistant content. Required when isEdited === true. */
  originalContent?: string;
  onEditClick?: () => void;
  onSave?: (content: string) => Promise<void>;
  isEditLoading?: boolean;
  editError?: string;
  assistantName?: string;
  avatarUrl?: string;
  /** Avatar data for the user who edited (defaults to current user). */
  editorUserId?: string;
  editorUserName?: string;
  editorUserEmail?: string;
  lastEditedAt?: string;

  /** Save functionality props */
  projectId?: string;
  notebookId?: string;
  notebookTitle?: string;
  onSaveToNotebook?: (files: File[], folderId?: string, shouldIndex?: boolean) => Promise<void>;
  selectedAssistant?: string;
  conversationContext?: {
    messageId: string;
    conversationId?: string;
    totalMessages: number;
  };
  canEdit?: boolean;

  /** Turn file tracking - files created/modified during this turn */
  turnFilesCreated?: string[];
  turnFilesModified?: string[];
  /** Callback when a turn file pill is clicked - receives the relative path */
  onTurnFileClick?: (relativePath: string) => void;
}

/**
 * AssistantCell displays a read-only assistant message with markdown rendering.
 * Shows streaming indicator when isStreaming is true.
 * Can be clicked to edit if it's the last message.
 */
export default function AssistantCell({ 
  content, 
  isLast, 
  isStreaming = false, 
  isEdited = false,
  originalContent,
  onEditClick,
  onSave,
  isEditLoading = false,
  editError,
  assistantName = 'Assistant',
  avatarUrl,
  editorUserId,
  editorUserName,
  editorUserEmail,
  lastEditedAt,
  projectId: _projectId,
  notebookId: _notebookId,
  notebookTitle,
  onSaveToNotebook,
  selectedAssistant,
  conversationContext,
  canEdit = false,
  turnFilesCreated,
  turnFilesModified,
  onTurnFileClick
}: AssistantCellProps) {
  // Debug streaming props
  if (isStreaming) {
    console.log('AssistantCell streaming props:', { content, isStreaming, isLast, assistantName });
  }

  const [isFull, setIsFull] = useState(false);
  const [isEditing, setIsEditing] = useState(false);
  const [showOriginal, setShowOriginal] = useState(false);
  const [showDiff, setShowDiff] = useState(false);
  const [showSaveDialog, setShowSaveDialog] = useState(false);
  const [isSaving, setIsSaving] = useState(false);

  // Mobile detection for touch-friendly interactions
  const [isMobile, setIsMobile] = useState(false);
  useEffect(() => {
    const checkMobile = () => setIsMobile(window.innerWidth < 768);
    checkMobile();
    window.addEventListener('resize', checkMobile);
    return () => window.removeEventListener('resize', checkMobile);
  }, []);

  const { showToast } = useToast();
  // Display-time decode for assistant text only.
  // Tradeoff: when a message contains surrogate-style unicode escapes, some
  // other escaped sequences in that same message may also render as characters
  // instead of remaining literal escape text.
  const decodedContent = useMemo(() => decodeAssistantUnicodeEscapes(content), [content]);
  const decodedOriginalContent = useMemo(
    () => (originalContent ? decodeAssistantUnicodeEscapes(originalContent) : undefined),
    [originalContent]
  );

  // Determine editor information to display
  const displayEditorInfo = { id: editorUserId, name: editorUserName, email: editorUserEmail };

  // Resolve absolute avatar URL
  let avatarSrc = avatarUrl;
  if (avatarSrc && !avatarSrc.startsWith('http')) {
    avatarSrc = resolveAgainstApiBase(avatarSrc).toString();
  }
  // Append projectId to avatar URLs if needed
  if (avatarSrc && _projectId && avatarSrc.includes('/api/') && !avatarSrc.includes('projectId=')) {
    avatarSrc = `${avatarSrc}${avatarSrc.includes('?') ? '&' : '?'}projectId=${_projectId}`;
  }

  // Determine if copy button should be visible
  const canCopy = decodedContent && decodedContent.trim().length > 0;

  // Determine if save button should be visible - if we have the save function and valid content, show it
  const canSaveToNotebook = onSaveToNotebook && canEdit && 
    !isStreaming &&
    decodedContent &&
    decodedContent.trim().length > 0;

  // Copy to clipboard functionality
  const handleCopyToClipboard = useCallback(async () => {
    try {
      if (navigator.clipboard && navigator.clipboard.writeText) {
        await navigator.clipboard.writeText(decodedContent);
        showToast({
          type: 'success',
          title: 'Content copied to clipboard',
          duration: 3000
        });
      } else {
        // Fallback for older browsers
        const textArea = document.createElement('textarea');
        textArea.value = decodedContent;
        textArea.style.position = 'fixed';
        textArea.style.left = '-999999px';
        textArea.style.top = '-999999px';
        document.body.appendChild(textArea);
        textArea.focus();
        textArea.select();
        
        try {
          const successful = document.execCommand('copy');
          if (successful) {
            showToast({
              type: 'success',
              title: 'Content copied to clipboard',
              duration: 3000
            });
          } else {
            throw new Error('Copy command failed');
          }
        } catch (err) {
          console.error('Fallback copy failed:', err);
          showToast({
            type: 'error',
            title: 'Copy failed',
            message: 'Unable to copy content. Please try selecting and copying manually.',
            duration: 5000
          });
        } finally {
          document.body.removeChild(textArea);
        }
      }
    } catch (error) {
      console.error('Failed to copy to clipboard:', error);
      showToast({
        type: 'error',
        title: 'Copy failed',
        message: 'Unable to copy content. Please try again.',
        duration: 5000
      });
    }
  }, [decodedContent, showToast]);

  // Save functionality
  const handleSave = () => {
    if (canSaveToNotebook) {
      setShowSaveDialog(true);
    }
  };

  // Handle save from dialog
  const handleSaveFromDialog = async (fileName: string, folderPath?: string) => {
    if (!onSaveToNotebook || !conversationContext) {
      throw new Error('Save functionality not available');
    }

    setIsSaving(true);
    try {
      // Create metadata with context for URL conversion
      const metadata: AssistantContentMetadata = {
        messageId: conversationContext.messageId,
        conversationId: conversationContext.conversationId,
        assistantName: assistantName,
        notebookTitle: notebookTitle,
        generatedAt: new Date().toISOString(),
        wordCount: decodedContent.trim().split(/\s+/).length,
        selectedAssistant: selectedAssistant,
        projectId: _projectId,
        notebookId: _notebookId
      };

      // Generate markdown file with relative URLs for portability
      const markdownFile = generateMarkdownFile(decodedContent, fileName, metadata);

      // Upload to notebook
      await onSaveToNotebook([markdownFile], folderPath, false);
      // Trigger immediate notebook files refresh without coupling state back from tree
      try { window.dispatchEvent(new Event('refresh-notebook-files')); } catch {}

      // Success is handled by the dialog's success toast
    } catch (error) {
      console.error('Save failed:', error);
      throw error; // Re-throw to let dialog handle the error
    } finally {
      setIsSaving(false);
    }
  };

  // Show original message overlay if requested (edited messages only)
  if (showOriginal) {
    const closeOriginal = () => setShowOriginal(false);
    const textToShow = decodedOriginalContent || decodedContent;
    return createPortal(
      <div className="fixed inset-0 z-50 bg-gray-50 overflow-auto select-text">
        {/* Close button */}
        <div className="fixed top-4 right-4 z-10">
          <button
            onClick={closeOriginal}
            className="p-1 hover:bg-gray-100 rounded text-gray-500 hover:text-gray-700"
            aria-label="Close original message view"
            title="Close"
          >
            <FaTimes className="w-4 h-4" />
          </button>
        </div>

        {/* Content wrapper */}
        <div className="h-full p-4 md:p-6 lg:p-8">
          <div className="h-full bg-white rounded-lg shadow-lg border border-gray-200 p-6 md:p-8 lg:p-12 overflow-auto">
            {/* Header */}
            <div className="mb-6">
              <h2 className="text-xl font-semibold mb-2">Original Assistant Message</h2>
              {editorUserName && lastEditedAt && (
                <p className="text-sm text-gray-500">Edited by {editorUserName} on {new Date(lastEditedAt).toLocaleString()}</p>
              )}
              {originalContent && (
                <button
                  onClick={() => setShowDiff(!showDiff)}
                  className="mt-3 text-blue-600 hover:underline text-sm focus:outline-none"
                >
                  {showDiff ? 'Hide diff' : 'Show diff'}
                </button>
              )}
            </div>

            <div className="prose prose-sm max-w-none">
              {showDiff && originalContent ? (
                <MessageDiff original={decodedOriginalContent || ''} edited={decodedContent} />
              ) : (
                <ChatMarkdownViewer text={textToShow} className="prose prose-sm max-w-none" projectId={_projectId} notebookId={_notebookId} onLinkClick={onTurnFileClick} turnFilesModified={turnFilesModified} turnFilesCreated={turnFilesCreated} />
              )}
            </div>
          </div>
        </div>
      </div>,
      document.body
    );
  }

  // Handle edit click
  const handleEditClick = () => {
    if (!canEdit) return;
    // If already in full screen, just switch to edit mode
    if (isFull) {
      setIsEditing(true);
    } else {
      // From regular view, open fullscreen editor
      setIsFull(true);
      setIsEditing(true);
    }
  };

  // Handle full-screen editing with FullScreenEditor
  if (isFull && isEditing && canEdit) {
    const handleSave = async (newContent: string) => {
      if (onSave) {
        await onSave(newContent);
      }
      setIsFull(false);
      setIsEditing(false);
    };

    const handleCancel = (_currentContent?: string) => {
      setIsFull(false);
      setIsEditing(false);
    };

    return (
      <FullScreenEditor
        content={decodedContent}
        onSave={handleSave}
        onCancel={handleCancel}
        mode="edit"
        placeholder="Edit assistant response..."
        isLoading={isEditLoading}
        error={editError}
        projectId={_projectId}
        notebookId={_notebookId}
      />
    );
  }

  // Handle full-screen viewing
  if (isFull && !isEditing) {
    return createPortal(
      <div className="fixed inset-0 z-50 bg-gray-50 overflow-auto select-text">
        {/* Content area */}
        <div className="h-full p-4 md:p-6 lg:p-8">
          <div className="h-full bg-white rounded-lg shadow-lg border border-gray-200 p-6 md:p-8 lg:p-12 overflow-auto relative">
            {/* Controls positioned inside content area */}
            <div className="absolute top-4 right-4 flex flex-col items-center space-y-2 z-10">
              <button
                onClick={() => setIsFull(false)}
                className="p-1 hover:bg-gray-100 rounded text-gray-500 hover:text-gray-700"
                aria-label="Exit full screen"
                title="Exit full screen"
              >
                <FaCompress className="w-4 h-4" />
              </button>
              
              {!isStreaming && isLast && onEditClick && canEdit && (
                <button
                  onClick={(e) => {
                    e.stopPropagation();
                    handleEditClick();
                  }}
                  className="p-1 hover:bg-gray-100 rounded text-gray-500 hover:text-gray-700"
                  aria-label="Edit message"
                  title="Edit message"
                >
                  <FaEdit className="w-4 h-4" />
                </button>
              )}
            </div>
            
            <ChatMarkdownViewer text={decodedContent} className="prose prose-sm lg:prose-base max-w-none" isStreaming={isStreaming} projectId={_projectId} notebookId={_notebookId} onLinkClick={onTurnFileClick} turnFilesModified={turnFilesModified} turnFilesCreated={turnFilesCreated} />
          </div>
        </div>
      </div>,
      document.body
    );
  }

  // ---------- Regular (non-fullscreen) cell ----------
  return (
    <>
      {/* Message content spanning from left into content area */}
      <div 
        className="col-span-2 relative border rounded-md bg-white p-2 sm:p-3 md:p-4 shadow-sm my-1 sm:my-2 w-full pr-8 sm:pr-10 md:pr-14 lg:pr-16 pl-8 sm:pl-10 md:pl-14 lg:pl-16"
        role="group" 
        aria-label="Assistant message"
        data-testid="message"
        data-role="assistant"
      >
        {/* Edited badge */}
        {isEdited && (
          <button
            type="button"
            onClick={(e) => { e.stopPropagation(); setShowOriginal(true); }}
            className="absolute -top-2 left-2 bg-blue-100 border border-blue-200 text-blue-600 text-[11px] font-medium px-1.5 py-0.5 rounded flex items-center space-x-1 hover:bg-blue-200 focus:outline-none"
            aria-label="View original assistant response"
          >
            <FaEdit className="w-3 h-3" />
            <span className="hidden sm:inline">Edited</span>
          </button>
        )}

        {/* Content */}
        <div className="mt-2 mb-2">
          {isStreaming && !decodedContent ? (
            <div className="flex items-center space-x-2">
              <div className="flex space-x-1">
                <div className="w-2 h-2 bg-gray-400 rounded-full animate-bounce"></div>
                <div className="w-2 h-2 bg-gray-400 rounded-full animate-bounce" style={{ animationDelay: '0.1s' }}></div>
                <div className="w-2 h-2 bg-gray-400 rounded-full animate-bounce" style={{ animationDelay: '0.2s' }}></div>
              </div>
              <span className="text-gray-500 text-sm">Thinking...</span>
            </div>
          ) : (
            <ChatMarkdownViewer text={decodedContent} className="prose-sm" isStreaming={isStreaming} maxImageHeight={450} projectId={_projectId} notebookId={_notebookId} onLinkClick={onTurnFileClick} turnFilesModified={turnFilesModified} turnFilesCreated={turnFilesCreated} />
          )}
        </div>

        {/* Turn file pills - show files created/modified during this turn */}
        {((turnFilesCreated && turnFilesCreated.length > 0) || (turnFilesModified && turnFilesModified.length > 0)) && (() => {
          // If a file appears in both created and modified, only show as "new" (created takes precedence)
          const createdSet = new Set(turnFilesCreated || []);
          const modifiedOnly = (turnFilesModified || []).filter(path => !createdSet.has(path));
          
          return (
            <div className="flex flex-wrap gap-1.5 mt-2 mb-1 pt-2 border-t border-gray-100">
              {turnFilesCreated?.map((filePath) => (
                <FilePill
                  key={`new-${filePath}`}
                  filePath={filePath}
                  type="created"
                  isMobile={isMobile}
                  onPreview={(path) => onTurnFileClick?.(path)}
                />
              ))}
              {modifiedOnly.map((filePath) => (
                <FilePill
                  key={`mod-${filePath}`}
                  filePath={filePath}
                  type="modified"
                  isMobile={isMobile}
                  onPreview={(path) => onTurnFileClick?.(path)}
                />
              ))}
            </div>
          );
        })()}
        
        {/* Action buttons container */}
        <div className="absolute top-1 right-3 flex flex-row items-center space-x-2">
          {/* Save button */}
          {canSaveToNotebook && (
            <button
              type="button"
              onClick={(e) => { e.stopPropagation(); handleSave(); }}
              className="p-1 hover:bg-gray-100 rounded text-gray-500 hover:text-gray-700 disabled:opacity-50"
              aria-label="Save to notebook"
              title="Save to notebook"
              disabled={isSaving}
            >
              <FaSave className="w-4 h-4" />
            </button>
          )}

          {/* Edit button */}
          {!isStreaming && isLast && onEditClick && canEdit && (
            <button
              type="button"
              onClick={(e) => { e.stopPropagation(); handleEditClick(); }}
              className="p-1 hover:bg-gray-100 rounded text-gray-500 hover:text-gray-700"
              aria-label="Edit message"
              title="Edit message"
            >
              <FaEdit className="w-4 h-4" />
            </button>
          )}

          {/* Copy button */}
          {canCopy && (
            <button
              type="button"
              onClick={(e) => { e.stopPropagation(); handleCopyToClipboard(); }}
              className="p-1 hover:bg-gray-100 rounded text-gray-500 hover:text-gray-700 disabled:opacity-50"
              aria-label="Copy to clipboard"
              title="Copy to clipboard"
            >
              <FaCopy className="w-4 h-4" />
            </button>
          )}

          {/* Full screen button */}
          <button
            type="button"
            onClick={(e) => { e.stopPropagation(); setIsFull(true); }}
            className="p-1 hover:bg-gray-100 rounded text-gray-500 hover:text-gray-700"
            aria-label="Full screen"
            title="Full screen"
          >
            <FaExpandAlt className="w-4 h-4" />
          </button>
        </div>
      </div>
      
      {/* Avatar column */}
      {isEdited ? (
        <div className="flex flex-col justify-start items-center pt-2 sm:pt-3 md:pt-4 space-y-1">
          {/* Assistant avatar button (opens original) */}
          <button
            type="button"
            onClick={() => setShowOriginal(true)}
            className="focus:outline-none"
            aria-label="View original assistant response"
          >
            <div className="h-5 w-5 sm:h-6 sm:w-6 lg:h-7 lg:w-7 rounded-full bg-green-100 flex items-center justify-center overflow-hidden ring-1 ring-gray-300">
              {avatarSrc ? (
                <img src={avatarSrc} alt={assistantName} className="w-full h-full object-cover" />
              ) : (
                <span className="text-green-600 text-[10px] sm:text-[11px] lg:text-xs font-medium">A</span>
              )}
            </div>
          </button>

          {/* Editor (user) avatar */}
          <UserAvatar
            userId={displayEditorInfo.id}
            userName={displayEditorInfo.name}
            userEmail={displayEditorInfo.email}
            size="xs"
          />
        </div>
      ) : (
        <div className="flex justify-center items-start pt-2 sm:pt-3 md:pt-4">
          <div className="h-8 w-8 sm:h-10 sm:w-10 md:h-12 md:w-12 lg:h-14 lg:w-14 rounded-full bg-green-100 flex items-center justify-center overflow-hidden">
            {avatarSrc ? (
              <img 
                src={avatarSrc} 
                alt={assistantName}
                className="w-full h-full object-cover"
              />
            ) : (
              <span className="text-green-600 font-medium text-sm sm:text-base lg:text-lg">A</span>
            )}
          </div>
        </div>
      )}

      {/* Save Assistant Content Dialog */}
      {showSaveDialog && (
        <SaveAssistantContentDialog
          isOpen={showSaveDialog}
          onClose={() => setShowSaveDialog(false)}
          onSave={handleSaveFromDialog}
          content={content}
          assistantName={assistantName}
          notebookTitle={notebookTitle}
          disabled={isSaving}
        />
      )}
    </>
  );
} 
