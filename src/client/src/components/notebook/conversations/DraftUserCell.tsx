import { useState, useEffect, useRef, useCallback, useMemo } from 'react';
import { useNotebook } from '../../../contexts/NotebookContext';
import { useParams } from 'react-router-dom';
import { notebookFilesApi } from '../../../services/notebookFiles';
import UserAvatar from '../../common/UserAvatar';
import FullScreenEditor from './FullScreenEditor';
import LexicalEditor, { LexicalEditorRef } from './LexicalEditor';
import { AssistantOption } from './AssistantSelector';
import { useConversation } from '../../../contexts/ConversationContext';
import { mapContentType } from '../../../utils/attachments';
import { PendingAttachment } from '../../../types/conversation';
import { resolveAgainstApiBase } from '../../../config/apiConfig';
import { useAudioRecorder } from '../../../hooks/useAudioRecorder';
import MicrophoneButton from '../../common/MicrophoneButton';
import CameraCapture from '../../common/CameraCapture';

/**
 * Normalizes markdown content for chat messages by converting paragraph breaks
 * (double newlines from Lexical) to single newlines. This ensures:
 * - Consistent storage format with minimal client
 * - Clean copy behavior (no extra line breaks when copying from display)
 */
function normalizeForChat(content: string): string {
  // Replace double+ newlines with single newlines
  // Preserve newlines in code blocks by processing outside of them
  const codeBlockRegex = /```[\s\S]*?```/g;
  const codeBlocks: string[] = [];
  
  // Extract code blocks
  const withPlaceholders = content.replace(codeBlockRegex, (match) => {
    codeBlocks.push(match);
    return `\x00CODE_BLOCK_${codeBlocks.length - 1}\x00`;
  });
  
  // Normalize newlines outside code blocks
  const normalized = withPlaceholders.replace(/\n\n+/g, '\n');
  
  // Restore code blocks
  return normalized.replace(/\x00CODE_BLOCK_(\d+)\x00/g, (_, index) => codeBlocks[parseInt(index)]);
}

interface DraftUserCellProps {
  value: string;
  onChange: (value: string) => void;
  onSend: (content: string) => void;
  disabled?: boolean; // legacy streaming flag
  canEdit?: boolean;
  isStreaming?: boolean;
  placeholder?: string;
  userId?: string;
  userName?: string;
  userEmail?: string;
  // Assistant selection props
  assistants?: AssistantOption[];
  onAssistantSelect?: (assistantName: string) => void;
  'data-tour-id'?: string;
}

/**
 * DraftUserCell provides textarea interface for composing new user messages.
 * Supports Ctrl+Enter to send, auto-resize, and @ assistant selection.
 * Displays user avatar with initials for consistency with UserCell.
 */
export default function DraftUserCell({ 
  value, 
  onChange, 
  onSend, 
  canEdit = true,
  isStreaming = false,
  userId,
  userName,
  userEmail,
  assistants = [],
  onAssistantSelect,
  'data-tour-id': dataTourId
}: DraftUserCellProps) {
  const [isFullScreen, setIsFullScreen] = useState(false);
  const [localContent, setLocalContent] = useState(value);
  const [showAssistantSelector, setShowAssistantSelector] = useState(false);
  const [atPosition, setAtPosition] = useState<{ x: number; y: number } | null>(null);
  const [atMentionStart, setAtMentionStart] = useState<number | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [selectedIndex, setSelectedIndex] = useState(0);
  const editorRef = useRef<LexicalEditorRef>(null);
  const editorContainerRef = useRef<HTMLDivElement>(null);

  // Track images currently uploading from clipboard paste
  const [uploadingImages, setUploadingImages] = useState<{ id: string; fileName: string }[]>([]);
  
  // Camera capture state
  const [isCameraOpen, setIsCameraOpen] = useState(false);

  // Access notebook / project IDs needed for file upload
  const { notebook } = useNotebook();
  const notebookCtxProjectId = notebook?.projectId;
  const notebookCtxNotebookId = notebook?.id;

  // Fallback to URL params if context not yet ready
  const routeParams = useParams<{ projectId: string; notebookId: string }>();
  const projectId = notebookCtxProjectId || routeParams.projectId;
  const notebookId = notebookCtxNotebookId || routeParams.notebookId;
  
  // Track the last synced value to avoid unnecessary syncs
  const lastSyncedValueRef = useRef(value);
  // Track the latest onChange function and localContent
  const onChangeRef = useRef(onChange);
  const localContentRef = useRef(localContent);

  const { pendingAttachments, addPendingAttachment, removePendingAttachment } = useConversation();

  // Speech-to-text hook
  const handleTranscriptionComplete = useCallback((text: string) => {
    if (editorRef.current) {
      editorRef.current.insertText(text);
      // Update local content after insertion
      const newContent = editorRef.current.getValue();
      setLocalContent(newContent);
    }
  }, []);

  const {
    isRecording,
    isProcessing: isTranscribing,
    duration: recordingDuration,
    isSupported: isSpeechSupported,
    startRecording,
    stopRecording,
  } = useAudioRecorder({
    maxDurationSeconds: 60,
    onTranscriptionComplete: handleTranscriptionComplete,
  });

  // Check if camera is supported
  const isCameraSupported = typeof navigator !== 'undefined'
    && navigator.mediaDevices
    && typeof navigator.mediaDevices.getUserMedia === 'function';

  // Handle camera capture completion
  const handleCameraCapture = async (blob: Blob, fileName: string) => {
    if (!projectId || !notebookId) {
      console.error('Cannot upload: Notebook not loaded');
      return;
    }

    const file = new File([blob], fileName, { type: 'image/jpeg' });
    const tempId = fileName;
    setUploadingImages(prev => [...prev, { id: tempId, fileName }]);

    try {
      const uploaded = await notebookFilesApi.uploadFiles(projectId, notebookId, [file], '', false);
      if (uploaded && uploaded.length > 0) {
        const fileDto = uploaded[0];
        addPendingAttachment({
          notebookFileId: fileDto.id,
          fileName: fileDto.fileName,
          uploadType: 'image'
        });
      }
    } catch (err) {
      console.error('Camera capture upload failed', err);
    } finally {
      setUploadingImages(prev => prev.filter(u => u.id !== tempId));
    }
  };

  // Derived state
  const isBusy = Boolean(isStreaming) || isTranscribing;
  const isReadOnly = !canEdit;

  // Filter assistants based on search query
  const filteredAssistants = useMemo(() => {
    if (!searchQuery.trim()) return assistants;
    
    const query = searchQuery.toLowerCase();
    return assistants.filter(assistant =>
      assistant.name.toLowerCase().includes(query) ||
      (assistant.model && assistant.model.toLowerCase().includes(query))
    );
  }, [assistants, searchQuery]);

  // Update refs when props/state change
  useEffect(() => {
    onChangeRef.current = onChange;
  }, [onChange]);

  useEffect(() => {
    localContentRef.current = localContent;
  }, [localContent]);

  // On first mount populate the editor with the current value
  useEffect(() => {
    if (editorRef.current) {
      editorRef.current.setValue(value);
      setLocalContent(value);
      lastSyncedValueRef.current = value;
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Sync external value into editor only when the incoming value truly differs
  useEffect(() => {
    if (editorRef.current && !isFullScreen && value !== lastSyncedValueRef.current) {
      console.log('[DraftUserCell] External value changed, syncing:', value);
      editorRef.current.setValue(value);
      setLocalContent(value);
      lastSyncedValueRef.current = value;
    }
  }, [value, isFullScreen]);

  // Handle key input for filtering
  const handleFilterInput = useCallback((event: KeyboardEvent) => {
    if (!showAssistantSelector) return;
    
    // Handle regular typing for filtering
    if (event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey) {
      const newQuery = searchQuery + event.key;
      setSearchQuery(newQuery);
      setSelectedIndex(0);
      event.preventDefault(); // Don't let the character go into the editor
    } else if (event.key === 'Backspace') {
      const newQuery = searchQuery.slice(0, -1);
      setSearchQuery(newQuery);
      setSelectedIndex(0);
      event.preventDefault(); // Don't let backspace affect the editor
    }
  }, [showAssistantSelector, searchQuery]);

  // Update local content when editor changes (isolated from global state)
  const handleEditorChange = useCallback(() => {
    if (editorRef.current) {
      const markdown = editorRef.current.getValue();
      setLocalContent(markdown);
    }
  }, []);

  useEffect(() => {
    if (!editorRef.current) return;
    const unregister = editorRef.current.registerChangeListener(() => {
      handleEditorChange();
    });
    return unregister;
  }, [handleEditorChange]);

  // Sync local content to global state when needed
  const syncToGlobalState = useCallback(() => {
    if (localContent !== lastSyncedValueRef.current) {
      onChangeRef.current(localContent);
      lastSyncedValueRef.current = localContent;
    }
  }, [localContent]);

  // Handle @ key press to show assistant selector
  const handleAtKeyPress = useCallback((event: KeyboardEvent) => {
    if (isReadOnly) return;
    if (event.key === '@' && assistants.length > 0 && !showAssistantSelector) {
      // Delay to let the @ character be inserted first
      setTimeout(() => {
        const selection = window.getSelection();
        if (selection && selection.rangeCount > 0) {
          const range = selection.getRangeAt(0);
          const rect = range.getBoundingClientRect();
          
          // Use viewport coordinates for fixed positioning
          let x = rect.left;
          let y = rect.bottom + 5;
          
          // Fallback: if rect is empty (0,0,0,0), use container position
          if (rect.width === 0 && rect.height === 0) {
            const containerRect = editorContainerRef.current?.getBoundingClientRect();
            if (containerRect) {
              x = containerRect.left + 20; // Small offset from left edge
              y = containerRect.top + 60; // Offset from top
            }
          }
          
          setAtPosition({ x, y });
          setAtMentionStart(localContent.length);
          setSearchQuery('');
          setSelectedIndex(0);
          setShowAssistantSelector(true);
        }
      }, 0);
    }
  }, [assistants.length, showAssistantSelector, localContent.length, isReadOnly]);

  // Handle keyboard navigation in assistant selector
  const handleSelectorKeyDown = useCallback((event: KeyboardEvent) => {
    if (!showAssistantSelector) return;

    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        setSelectedIndex(prev => Math.min(prev + 1, filteredAssistants.length - 1));
        break;
      case 'ArrowUp':
        event.preventDefault();
        setSelectedIndex(prev => Math.max(prev - 1, 0));
        break;
      case 'Enter':
      case 'Tab':
        event.preventDefault();
        if (filteredAssistants[selectedIndex]) {
          handleAssistantSelection(filteredAssistants[selectedIndex].name);
        }
        break;
      case 'Escape':
        event.preventDefault();
        // Just close the dropdown, preserve the @ character
        setShowAssistantSelector(false);
        setAtPosition(null);
        setAtMentionStart(null);
        setSearchQuery('');
        setSelectedIndex(0);
        // Refocus the editor and position cursor at the end
        setTimeout(() => {
          if (editorContainerRef.current) {
            const contentEditable = editorContainerRef.current.querySelector('[contenteditable="true"]');
            if (contentEditable) {
              // Prevent focus from causing a programmatic scroll that could disable auto-scroll
              try {
                (contentEditable as any).focus({ preventScroll: true });
              } catch {
                (contentEditable as HTMLElement).focus();
              }
              
              // Position cursor at the end of the content
              const selection = window.getSelection();
              if (selection) {
                const range = document.createRange();
                range.selectNodeContents(contentEditable);
                range.collapse(false); // false = collapse to end
                selection.removeAllRanges();
                selection.addRange(range);
              }
            }
          }
        }, 0);
        break;
    }
     }, [showAssistantSelector, filteredAssistants, selectedIndex]); // eslint-disable-line react-hooks/exhaustive-deps

  // Handle assistant selection
  const handleAssistantSelection = useCallback((assistantName: string) => {
    if (editorRef.current && atMentionStart !== null) {
      // Get current content and remove the @ and any search characters
      const currentContent = editorRef.current.getValue();
      const beforeAt = currentContent.substring(0, atMentionStart);
      // Skip @ and the search query length to get the content after
      const afterAtAndSearch = currentContent.substring(atMentionStart + 1 + searchQuery.length);
      
      // Remove @ and search text, just keep the content before and after
      const newContent = beforeAt + afterAtAndSearch;
      
      // Update editor and local state
      editorRef.current.setValue(newContent);
      setLocalContent(newContent);
      
      // Update global state
      onChangeRef.current(newContent);
      lastSyncedValueRef.current = newContent;
      
      // Set the selected assistant (this is the main action)
      if (onAssistantSelect) {
        onAssistantSelect(assistantName);
      }
    }
    
    // Hide selector and reset state
    setShowAssistantSelector(false);
    setAtPosition(null);
    setAtMentionStart(null);
    setSearchQuery('');
    setSelectedIndex(0);
  }, [atMentionStart, onAssistantSelect, searchQuery]);

  // Close assistant selector when clicking outside
  const handleClickOutside = useCallback((event: MouseEvent) => {
    if (showAssistantSelector && editorContainerRef.current && 
        !editorContainerRef.current.contains(event.target as Node)) {
      setShowAssistantSelector(false);
      setAtPosition(null);
      setAtMentionStart(null);
      setSearchQuery('');
      setSelectedIndex(0);
    }
  }, [showAssistantSelector]);

  // Set up keyboard and click event listeners
  useEffect(() => {
    document.addEventListener('mousedown', handleClickOutside);
    
    return () => {
      document.removeEventListener('mousedown', handleClickOutside);
    };
  }, [handleClickOutside]);

  const handleSend = (content: string) => {
    const hasText = Boolean(content && content.trim());
    const hasAttachments = pendingAttachments.length > 0;
    if (!hasText && !hasAttachments) return;
    // Normalize content: convert double newlines to single for consistent storage
    const normalizedContent = normalizeForChat(content);
    // Sync to global state before sending
    if (normalizedContent !== lastSyncedValueRef.current) {
      onChangeRef.current(normalizedContent);
      lastSyncedValueRef.current = normalizedContent;
    }
    onSend(normalizedContent);
    // CRITICAL: Clear local content after sending (Fix B)
    setLocalContent('');
    if (editorRef.current) {
      editorRef.current.setValue('');
    }
    lastSyncedValueRef.current = '';
  };

  const handleFullScreenSave = (content: string) => {
    // Normalize content: convert double newlines to single for consistent storage
    const normalizedContent = normalizeForChat(content);
    // Sync to global state and send
    onChangeRef.current(normalizedContent);
    lastSyncedValueRef.current = normalizedContent;
    onSend(normalizedContent);
    setIsFullScreen(false);
    setLocalContent('');
    lastSyncedValueRef.current = '';
  };

  const handleFullScreenCancel = (currentContent?: string) => {
    if (currentContent !== undefined) {
      // Sync the current editor content to global state
      onChangeRef.current(currentContent);
      setLocalContent(currentContent);
      lastSyncedValueRef.current = currentContent;
    }
    setIsFullScreen(false);
  };

  const handleFullScreenOpen = () => {
    // Sync current local content to global state when transitioning to fullscreen
    syncToGlobalState();
    setIsFullScreen(true);
  };

  /* ---------------------------------- Paste ---------------------------------- */
  // Attach paste handler on editor container
  useEffect(() => {
    const container = editorContainerRef.current;
    if (!container) return;

    const handlePaste = async (e: ClipboardEvent) => {
      if (isReadOnly || isBusy) return;

      const items = Array.from(e.clipboardData?.items ?? []);
      const imageItems = items.filter(it => it.kind === 'file' && it.type.startsWith('image/'));
      if (imageItems.length === 0) {
        return; // default paste behaviour
      }

      // We handle image upload ourselves – suppress default
      e.preventDefault();

      for (const item of imageItems) {
        const file = item.getAsFile();
        if (!file) continue;

        // Ensure unique filename to avoid duplicate-path conflicts on server
        const originalName = file.name && file.name.trim() !== '' ? file.name : 'pasted-image.png';
        const extMatch = originalName.match(/\.([a-zA-Z0-9]{1,8})$/);
        const ext = extMatch ? extMatch[1] : 'png';
        const uniqueName = `pasted-${Date.now()}-${Math.random().toString(36).slice(2,6)}.${ext}`;

        const fileForUpload = new File([file], uniqueName, { type: file.type || `image/${ext}` });

        const tempId = uniqueName; // use uniqueName as temp id
        setUploadingImages(prev => [...prev, { id: tempId, fileName: uniqueName }]);

        try {
          if (!projectId || !notebookId) throw new Error('Notebook not loaded');
          const uploaded = await notebookFilesApi.uploadFiles(projectId, notebookId, [fileForUpload], '', false);
          if (uploaded && uploaded.length > 0) {
            const fileDto = uploaded[0];
            addPendingAttachment({
              notebookFileId: fileDto.id,
              fileName: fileDto.fileName,
              uploadType: 'image'
            });
          }
        } catch (err) {
          console.error('Image paste upload failed', err);
          // Silent failure – simply drop the chip
        } finally {
          setUploadingImages(prev => prev.filter(u => u.id !== tempId));
        }
      }
    };

    container.addEventListener('paste', handlePaste);
    return () => container.removeEventListener('paste', handlePaste);
  }, [projectId, notebookId, isReadOnly, isBusy, addPendingAttachment]);

  // Get the current content from the editor
  const getCurrentContent = () => {
    const content = editorRef.current ? editorRef.current.getValue() : localContent;
    return content;
  };

  // Preserve unsaved changes on unmount only (no dependencies to avoid re-renders)
  useEffect(() => {
    return () => {
      // On actual unmount, check if there are unsaved changes
      const currentContent = editorRef.current?.getValue() || localContentRef.current;
      if (currentContent !== lastSyncedValueRef.current) {
        // This will only run once on unmount, not on every render
        onChangeRef.current(currentContent);
      }
    };
  }, []); // Empty dependencies - only runs on mount/unmount

  const handleDrop = useCallback((e: React.DragEvent<HTMLDivElement>) => {
    if (isReadOnly || isBusy) return;
    e.preventDefault();
    const fileId = e.dataTransfer.getData('application/x-notebook-file-id') || e.dataTransfer.getData('text/plain');
    const fileName = e.dataTransfer.getData('application/x-notebook-file-name');
    if (fileId && fileName) {
      addPendingAttachment({
        notebookFileId: fileId,
        fileName,
        uploadType: mapContentType(fileName),
      });
    }
  }, [isReadOnly, isBusy, addPendingAttachment]);

  return (
    <>
      {/* Render fullscreen editor on top when active */}
      {isFullScreen && (
        <FullScreenEditor
          content={getCurrentContent()}
          onSave={handleFullScreenSave}
          onCancel={handleFullScreenCancel}
          mode="compose"
          placeholder="Write your message..."
          isLoading={isBusy}
          projectId={notebookCtxProjectId}
          notebookId={notebookCtxNotebookId}
        />
      )}

      {/* Always render the inline editor but hide it when in fullscreen */}
      <div style={{ display: isFullScreen ? 'none' : 'contents' }}>
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
        
        {/* Message composition spanning into the third column */}
        <div 
          className="col-span-2 relative border rounded-md bg-white shadow-sm my-2 w-full border-blue-200 focus-within:border-blue-400"
          role="group" 
          aria-label="Compose message"
          data-tour-id={dataTourId}
          onDragOver={(e) => {
            e.preventDefault();
          }}
          onDrop={handleDrop}
        >
          {/* Lexical inline editor */}
          {/*
            Use onKeyDownCapture so that we stop the key event in the *capture* phase.
            This prevents the newline that Lexical (or the browser) may insert before the
            bubbling phase where our original handler ran.  We still respect Ctrl ⌃ / ⌘
            modifiers and suppress the default behaviour entirely, submitting the message
            without introducing the unwanted line-feed.
          */}
          <div
            ref={editorContainerRef}
            className="p-2 sm:p-3 md:p-4 relative"
            onKeyDownCapture={(e) => {
              if (isReadOnly) return;

              // Handle assistant selector events first
              // We pass the event as any because the handlers expect native KeyboardEvent 
              // but here we have React.KeyboardEvent. They are compatible for our usage.
              handleAtKeyPress(e as any);
              handleSelectorKeyDown(e as any);
              handleFilterInput(e as any);

              if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
                e.preventDefault();
                e.stopPropagation();
                const content = getCurrentContent();
                const hasText = Boolean(content?.trim());
                const hasAttachments = pendingAttachments.length > 0;
                if (hasText || hasAttachments) {
                  handleSend(content);
                }
              }
            }}
          >
            <LexicalEditor
              ref={editorRef}
              placeholder="Write your message..."
              autoFocus={true}
              showToolbar={true}
              className="min-h-[200px]"
              readOnly={isReadOnly || isBusy}
              projectId={notebookCtxProjectId}
              notebookId={notebookCtxNotebookId}
              fullScreenButton={{
                onClick: handleFullScreenOpen
              }}
            />
            
                        {/* Assistant selector dropdown */}
            {showAssistantSelector && atPosition && assistants.length > 0 && (
              <div
                className="fixed z-[9999] bg-white border border-gray-200 rounded-md shadow-lg min-w-[280px] max-w-sm max-h-80"
                style={{
                  left: `${Math.min(atPosition.x, window.innerWidth - 320)}px`,
                  top: atPosition.y + 300 > window.innerHeight ? `${atPosition.y - 320}px` : `${atPosition.y}px`,
                  position: 'fixed',
                }}
              >
                <div className="p-2 border-b border-gray-100">
                  <div className="text-sm text-gray-500 font-medium">
                    Select Guide ({filteredAssistants.length} of {assistants.length})
                  </div>
                </div>
                <div className="max-h-60 overflow-y-auto">
                  {filteredAssistants.length > 0 ? filteredAssistants.map((assistant, index) => {
                      // Resolve absolute avatar URL (same logic as working components)
                      let avatarSrc = assistant.avatarUrl;
                      if (avatarSrc && !avatarSrc.startsWith('http')) {
                        avatarSrc = resolveAgainstApiBase(avatarSrc).toString();
                      }
                      
                      const isSelected = index === selectedIndex;
                      
                      return (
                        <button
                          key={assistant.name}
                          onClick={() => handleAssistantSelection(assistant.name)}
                          className={`w-full flex items-center gap-3 px-3 py-2 text-left text-sm focus:outline-none transition-colors duration-150 ${
                            isSelected 
                              ? 'bg-blue-50 border-l-2 border-blue-500' 
                              : 'hover:bg-gray-50'
                          }`}
                        >
                          {avatarSrc && (
                            <img
                              src={avatarSrc}
                              alt={`${assistant.name} avatar`}
                              className="h-9 w-9 rounded-full object-cover flex-shrink-0"
                            />
                          )}
                          <div className="flex-1">
                            <div className={`font-medium text-base ${isSelected ? 'text-blue-900' : 'text-gray-900'}`}>
                              {assistant.name}
                            </div>
                            {/* Model indicator removed */}
                          </div>
                        </button>
                      );
                                          }) : (
                        <div className="px-4 py-8 text-center text-gray-500 text-base">
                          No guides found
                        </div>
                      )}
                  </div>
                </div>
              )}
            
            {(uploadingImages.length > 0 || pendingAttachments.length > 0) && (
              <div className="flex flex-wrap gap-2 px-3 pb-2">
                {/* Uploading placeholders */}
                {uploadingImages.map(up => (
                  <div key={up.id} className="flex items-center bg-gray-100 rounded px-2 py-1 text-xs opacity-50 animate-pulse">
                    <span className="truncate max-w-[120px]" title={up.fileName}>{up.fileName}</span>
                  </div>
                ))}

                {/* Pending attachments */}
                {pendingAttachments.map((att: PendingAttachment) => (
                  <div key={att.notebookFileId} className="flex items-center bg-gray-100 rounded px-2 py-1 text-xs">
                    <span className="truncate max-w-[120px]" title={att.fileName}>{att.fileName}</span>
                    <button onClick={() => removePendingAttachment(att.notebookFileId)} className="ml-1 text-gray-400 hover:text-red-600" type="button">×</button>
                  </div>
                ))}
              </div>
            )}

            {/* Action buttons */}
            <div className="flex items-center justify-between mt-3 pt-3 border-t border-gray-100">
              <div className="text-sm text-gray-500">
                {/* Keyboard shortcuts - hidden on mobile */}
                {!isReadOnly && (
                  <span className="hidden md:inline">
                    <kbd className="px-2 py-1 bg-gray-100 rounded text-xs">Ctrl+Enter</kbd> to send
                    {assistants.length > 0 && (
                      <span className="ml-3">
                        <kbd className="px-2 py-1 bg-gray-100 rounded text-xs">@</kbd> to mention guide
                      </span>
                    )}
                  </span>
                )}
                {isReadOnly && (
                  <span className="text-sm text-gray-500">Read-only</span>
                )}
              </div>
              <div className="flex items-center space-x-2">
                {/* Camera button */}
                {!isReadOnly && isCameraSupported && (
                  <button
                    type="button"
                    onClick={() => setIsCameraOpen(true)}
                    disabled={isBusy}
                    className="p-1.5 rounded hover:bg-gray-100 text-gray-600 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
                    title="Take photo"
                    aria-label="Take photo"
                  >
                    <svg
                      xmlns="http://www.w3.org/2000/svg"
                      viewBox="0 0 24 24"
                      fill="currentColor"
                      className="w-5 h-5"
                    >
                      <path d="M12 9a3.75 3.75 0 100 7.5A3.75 3.75 0 0012 9z" />
                      <path fillRule="evenodd" d="M9.344 3.071a49.52 49.52 0 015.312 0c.967.052 1.83.585 2.332 1.39l.821 1.317c.24.383.645.643 1.11.71.386.054.77.113 1.152.177 1.432.239 2.429 1.493 2.429 2.909V18a3 3 0 01-3 3H4.5a3 3 0 01-3-3V9.574c0-1.416.997-2.67 2.429-2.909.382-.064.766-.123 1.152-.177a1.56 1.56 0 001.11-.71l.821-1.317a2.35 2.35 0 012.332-1.39zM12 12.75a2.25 2.25 0 100 4.5 2.25 2.25 0 000-4.5z" clipRule="evenodd" />
                    </svg>
                  </button>
                )}
                
                {/* Speech-to-text button */}
                {!isReadOnly && isSpeechSupported && (
                  <MicrophoneButton
                    isRecording={isRecording}
                    isProcessing={isTranscribing}
                    duration={recordingDuration}
                    isSupported={isSpeechSupported}
                    disabled={isStreaming}
                    onStartRecording={startRecording}
                    onStopRecording={stopRecording}
                  />
                )}
                {isStreaming && !isTranscribing && (
                  <>
                    <div className="w-4 h-4 border-2 border-blue-600 border-t-transparent rounded-full animate-spin"></div>
                    <span className="text-sm text-gray-600">Sending...</span>
                  </>
                )}
                {!isReadOnly && (
                  <button
                    onClick={() => {
                      const content = getCurrentContent();
                      handleSend(content);
                    }}
                  disabled={isBusy || (!(getCurrentContent()?.trim() || value?.trim()) && pendingAttachments.length === 0)}
                    className="px-4 py-1.5 text-sm bg-blue-600 text-white rounded hover:bg-blue-700 disabled:opacity-50"
                  >
                    Send
                  </button>
                )}
              </div>
            </div>
          </div>
        </div>
      </div>
      
      {/* Camera Capture Modal */}
      <CameraCapture
        isOpen={isCameraOpen}
        onClose={() => setIsCameraOpen(false)}
        onCapture={handleCameraCapture}
      />
    </>
  );
} 
