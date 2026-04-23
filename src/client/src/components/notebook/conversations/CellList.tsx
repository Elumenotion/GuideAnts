import React, { useEffect, useRef, useState, useCallback, useMemo } from 'react';
import { MessageDto } from '../../../types/conversation';
import UserCell from './UserCell';
import AssistantCell from './AssistantCell';

import DraftUserCell from './DraftUserCell';
import { userService, UserInfo } from '../../../services/userService';
import { useNotebook } from '../../../contexts/NotebookContext';
import { useConversation } from '../../../contexts/ConversationContext';
import { useConversationStore } from '../../../store/conversationStore';
import WorkflowSection from './WorkflowSection';
import { AssistantOption } from './AssistantSelector';
import { AttachedFile } from '../../../types/conversation';
import { useNotebookFilesPolling } from '../../../hooks/useNotebookFilesPolling';
import { NotebookFolderTreeDto } from '../../../types/notebook';

// Debounce utility hook
function useDebounce<T extends (...args: any[]) => any>(
  callback: T,
  delay: number
): T {
  const timeoutRef = useRef<NodeJS.Timeout | null>(null);

  const debouncedCallback = useCallback(
    (...args: Parameters<T>) => {
      if (timeoutRef.current) {
        clearTimeout(timeoutRef.current);
      }

      timeoutRef.current = setTimeout(() => {
        callback(...args);
      }, delay);
    },
    [callback, delay]
  ) as T;

  // Cleanup timeout on unmount
  useEffect(() => {
    return () => {
      if (timeoutRef.current) {
        clearTimeout(timeoutRef.current);
      }
    };
  }, []);

  return debouncedCallback;
}

/** True if the scroll viewport was at (or effectively at) the bottom before a layout change. */
function isViewportAtBottom(el: HTMLElement, thresholdPx = 8): boolean {
  return el.scrollHeight - el.scrollTop - el.clientHeight < thresholdPx;
}

interface CellListProps {
  messages: MessageDto[];
  isStreaming: boolean;
  streamingMode?: 'at-rest' | 'sending' | 'observing';
  draftUserContent: string;
  editingAssistantId?: string;
  selectedAssistant?: string;
  assistants: AssistantOption[];
  conversationStarters: string[];
  onDraftChange: (content: string) => void;
  onSendMessage: (content: string) => void;
  onEditAssistant: (messageId: string) => void;
  onSaveAssistant: (messageId: string, content: string) => Promise<void>;
  onAssistantSelect: (assistantName: string) => void;

  onUndo: () => void;
  onEditUserMessage?: (messageId: string, content: string) => void;
  editError?: string;
  isEditLoading?: boolean;
  conversationId?: string;
  turnBasedMode?: boolean;
  onPreviewFile?: (fileId: string) => void;
  /** Handler to preview a file by its relative path (for turn file pills) */
  onPreviewFileByPath?: (relativePath: string) => void;
  canEdit?: boolean;
  'data-tour-id'?: string;
}

// Wrapper component to handle async user data loading
function UserCellWrapper({ 
  message, 
  isLast, 
  onUndo,
  onEdit,
  getUserInfo,
  attachments,
  onPreviewFile,
  projectId,
  notebookId
}: { 
  message: MessageDto; 
  isLast: boolean; 
  onUndo?: () => void;
  onEdit?: (content: string) => void;
  getUserInfo: (userId?: string) => Promise<UserInfo | null>;
  attachments?: AttachedFile[];
  onPreviewFile?: (fileId: string) => void;
  projectId?: string;
  notebookId?: string;
}) {
  const [userInfo, setUserInfo] = useState<UserInfo | null>(null);

  useEffect(() => {
    const loadUserInfo = async () => {
      const info = await getUserInfo(message.userId);
      setUserInfo(info);
    };
    loadUserInfo();
  }, [message.userId, getUserInfo]);

  return (
    <UserCell
      key={message.id}
      content={message.content}
      isLast={isLast}
      onUndo={onUndo}
      onEdit={onEdit}
      userId={userInfo?.id}
      userName={userInfo?.name}
      userEmail={userInfo?.email}
      attachments={attachments}
      onPreviewFile={onPreviewFile}
      projectId={projectId}
      notebookId={notebookId}
    />
  );
}

// Wrapper component to handle async editor user data loading for AssistantCell
function AssistantCellWrapper({ 
  message, 
  isLast,
  isStreaming,
  messageAssistant,
  onEditClick,
  onSaveAssistant,
  isEditLoading,
  editError,
  getUserInfo,
  conversationId,
  totalMessages,
  canEdit,
  onTurnFileClick,
  isValidFilePath
}: { 
  message: MessageDto; 
  isLast: boolean;
  isStreaming: boolean;
  messageAssistant: AssistantOption | undefined;
  onEditClick?: () => void;
  onSaveAssistant: (messageId: string, content: string) => Promise<void>;
  isEditLoading: boolean;
  editError?: string;
  getUserInfo: (userId?: string) => Promise<UserInfo | null>;
  conversationId?: string;
  totalMessages: number;
  canEdit?: boolean;
  onTurnFileClick?: (relativePath: string) => void;
  /** Filter function to check if a file path exists in the notebook tree */
  isValidFilePath?: (path: string) => boolean;
}) {
  const [editorInfo, setEditorInfo] = useState<UserInfo | null>(null);
  
  // Get notebook context for save functionality
  const { 
    notebook,
    projectId,
    notebookId: contextNotebookId,
    uploadFiles 
  } = useNotebook();

  // Get conversation context at the top level to follow Rules of Hooks
  const convCtxMaybe = (() => {
    try { return useConversation(); } catch { return undefined; }
  })();

  // Get current user for immediate fallback
  const currentUser = useMemo(() => {
    if (convCtxMaybe?.userProfiles) {
      // Find current user in profiles (usually the one with name/email)
      const currentUserProfile = Object.values(convCtxMaybe.userProfiles).find(
        (profile: any) => profile.name && profile.email
      );
      if (currentUserProfile) {
        return currentUserProfile;
      }
    }
    
    return null;
  }, [convCtxMaybe?.userProfiles]);

  useEffect(() => {
    const loadEditorInfo = async () => {
      // Only load editor info if message is edited and has a userId
      if (message.isEdited && message.userId) {
        const info = await getUserInfo(message.userId);
        setEditorInfo(info);
      } else {
        setEditorInfo(null);
      }
    };
    loadEditorInfo();
  }, [message.isEdited, message.userId, getUserInfo]);

  // Use current user as fallback when editor info is not available
  const displayEditorInfo = editorInfo || (message.isEdited ? currentUser : null);

  return (
    <AssistantCell
      key={message.id}
      content={message.content}
      isLast={isLast}
      isStreaming={isStreaming}
      isEdited={message.isEdited}
      originalContent={message.originalContent}
      onEditClick={onEditClick}
      onSave={async (c) => onSaveAssistant(message.id, c)}
      isEditLoading={isEditLoading}
      editError={editError}
      assistantName={messageAssistant?.name}
      avatarUrl={messageAssistant?.avatarUrl}
      editorUserId={displayEditorInfo?.id}
      editorUserName={displayEditorInfo?.name}
      editorUserEmail={displayEditorInfo?.email}
      lastEditedAt={message.lastEditedAt}
      // Save functionality props
      projectId={projectId}
      notebookId={contextNotebookId}
      notebookTitle={notebook?.title}
      onSaveToNotebook={uploadFiles}
      selectedAssistant={messageAssistant?.name}
      conversationContext={{
        messageId: message.id,
        conversationId: conversationId,
        totalMessages: totalMessages,
      }}
      canEdit={Boolean(canEdit)}
      // Turn file tracking - filter to only show files that exist in the notebook tree
      turnFilesCreated={isValidFilePath ? message.turnFilesCreated?.filter(isValidFilePath) : message.turnFilesCreated}
      turnFilesModified={isValidFilePath ? message.turnFilesModified?.filter(isValidFilePath) : message.turnFilesModified}
      onTurnFileClick={onTurnFileClick}
    />
  );
}

/**
 * CellList manages the vertical stack of conversation cells with auto-scroll behavior.
 * Handles cell rendering, edit state management, and draft composition.
 */
const CellList = React.memo(function CellList({
  messages,
  isStreaming,
  streamingMode = 'at-rest',
  draftUserContent,
  editingAssistantId,
  selectedAssistant,
  assistants,
  conversationStarters,
  onDraftChange,
  onSendMessage,
  onEditAssistant,
  onSaveAssistant,
  onAssistantSelect,

  onUndo,
  onEditUserMessage,
  editError,
  isEditLoading = false,
  conversationId,
  turnBasedMode = false,
  onPreviewFile,
  onPreviewFileByPath,
  canEdit = false,
  'data-tour-id': dataTourId
}: CellListProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  //const bottomAnchorRef = useRef<HTMLDivElement>(null);
  // Always start with auto-scroll enabled for each conversation
  const [shouldAutoScroll, setShouldAutoScroll] = useState(true);
  const shouldAutoScrollRef = useRef(shouldAutoScroll);
  const lastScrollTopRef = useRef(0);
  // Initialization window and user-interaction guard to prevent programmatic/layout scrolls from disabling auto-scroll
  const isInitializingRef = useRef(true);
  const userInteractedRef = useRef(false);
  // Track last known height to detect growth and coalesce scroll operations
  const lastHeightRef = useRef(0);
  const rafIdRef = useRef<number | null>(null);
  // Track previous streaming flag to re-enable auto-follow at the start of a new stream
  const prevIsStreamingRef = useRef<boolean>(false);
  // Track if we're performing a manual scroll-to-bottom to prevent effect conflicts
  const isManualScrollingRef = useRef(false);
  /** Mirrors whether the user is actually viewing the bottom; updated on scroll (not just shouldAutoScroll). */
  const wasViewingBottomRef = useRef(true);
  
  const [hasOverflow, setHasOverflow] = useState(false);

  const convCtxMaybe = (() => {
    try { return useConversation(); } catch { return undefined; }})();

  // Get notebook context for projectId/notebookId
  const { projectId, notebookId: contextNotebookId } = useNotebook();

  // Get folder tree to validate which files actually exist (for filtering turn file pills)
  const { folderTree } = useNotebookFilesPolling({
    projectId: projectId || '',
    notebookId: contextNotebookId || '',
    enabled: Boolean(projectId && contextNotebookId),
  });

  // Helper to collect all file paths from the folder tree
  const collectFilePaths = useCallback((node: NotebookFolderTreeDto | null): Set<string> => {
    const paths = new Set<string>();
    if (!node) return paths;
    
    // Add files in this folder
    for (const file of node.files) {
      // Add various path formats that might be used
      paths.add(file.relativePath);
      paths.add(file.fileName);
      // Also add without Output/ prefix if present
      if (file.relativePath.startsWith('Output/')) {
        paths.add(file.relativePath.substring(7));
      }
    }
    
    // Recurse into subfolders
    for (const subFolder of node.subFolders) {
      const subPaths = collectFilePaths(subFolder);
      subPaths.forEach(p => paths.add(p));
    }
    
    return paths;
  }, []);

  // Memoize the set of valid file paths
  const validFilePaths = useMemo(() => collectFilePaths(folderTree), [folderTree, collectFilePaths]);

  // Filter function to check if a file path exists in the tree
  const isValidFilePath = useCallback((filePath: string): boolean => {
    if (!filePath) return false;
    // Check exact match or common variations
    return validFilePaths.has(filePath) || 
           validFilePaths.has(`Output/${filePath}`) ||
           validFilePaths.has(filePath.replace(/^\.\.\//, ''));
  }, [validFilePaths]);

  const [currentUser, setCurrentUser] = useState<UserInfo | null>(null);

  const [userCache, setUserCache] = useState<Map<string, UserInfo>>(() => {
    const init = new Map<string, UserInfo>();
    if (convCtxMaybe?.userProfiles) {
      Object.entries(convCtxMaybe.userProfiles).forEach(([id, info]) => {
        if (id) init.set(id, info as UserInfo);
      });
    }
    return init;
  });
  // Removed blur overlay state

  // Keep ref in sync with state
  useEffect(() => {
    shouldAutoScrollRef.current = shouldAutoScroll;
  }, [shouldAutoScroll]);

  // On conversation change, re-enable auto-scroll and allow layout to settle before considering user scroll
  useEffect(() => {
    if (!conversationId) return;
    setShouldAutoScroll(true);
    wasViewingBottomRef.current = true;
    userInteractedRef.current = false;
    isInitializingRef.current = true;
    requestAnimationFrame(() => {
      requestAnimationFrame(() => {
        setTimeout(() => { isInitializingRef.current = false; }, 100);
      });
    });
  }, [conversationId]);

  // Mark user interaction to distinguish programmatic/layout scrolls from real user scrolls
  useEffect(() => {
    const el = containerRef.current;
    const markUserScroll = () => { userInteractedRef.current = true; };
    el?.addEventListener('wheel', markUserScroll as any, { passive: true } as any);
    el?.addEventListener('touchstart', markUserScroll as any, { passive: true } as any);
    return () => {
      el?.removeEventListener('wheel', markUserScroll as any);
      el?.removeEventListener('touchstart', markUserScroll as any);
    };
  }, []);

  // Immediate scroll listener to update bottom proximity and disable auto-scroll as soon as user moves away
  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    const onScrollImmediate = () => {
      const currentScrollTop = el.scrollTop;
      const isAtBottom = isViewportAtBottom(el, 5);
      wasViewingBottomRef.current = isAtBottom;
      
      // Only disable auto-scroll if user scrolled AND moved away from bottom
      if (userInteractedRef.current && shouldAutoScrollRef.current && !isAtBottom) {
        setShouldAutoScroll(false);
      }
      
      lastScrollTopRef.current = currentScrollTop;
    };
    el.addEventListener('scroll', onScrollImmediate, { passive: true } as any);
    return () => el.removeEventListener('scroll', onScrollImmediate as any);
  }, [conversationId]);

  // Re-enable auto-follow strictly at the start of a new stream
  useEffect(() => {
    const startedStreaming = isStreaming && !prevIsStreamingRef.current;
    prevIsStreamingRef.current = isStreaming;
    if (startedStreaming) {
      setShouldAutoScroll(true);
      wasViewingBottomRef.current = true;
      userInteractedRef.current = false;
    }
  }, [isStreaming]);

  // Reset auto-scroll when conversation content changes (even if same length)
  const prevFirstMessageId = useRef<string>('');
  useEffect(() => {
    const firstMessageId = messages[0]?.id || '';
    // If the first message ID changed, it's a different conversation
    if (firstMessageId && firstMessageId !== prevFirstMessageId.current) {
      setShouldAutoScroll(true);
      wasViewingBottomRef.current = true;
      prevFirstMessageId.current = firstMessageId;
    }
  }, [messages]);

  // When streaming ends, snap to bottom once after content settles
  useEffect(() => {
    if (isStreaming) return;
    const container = containerRef.current;
    if (!container) return;
    const id = setTimeout(() => {
      if (!shouldAutoScrollRef.current) return;
      container.scrollTop = container.scrollHeight;
    }, 100);
    return () => clearTimeout(id);
  }, [isStreaming]);

  // Evaluate overflow whenever messages or streaming state changes
  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;
    const overflowNow = container.scrollHeight - container.clientHeight > 1;
    if (overflowNow !== hasOverflow) {
      setHasOverflow(overflowNow);
    }
  }, [messages.length, isStreaming]);


  // Simple scroll handler to determine if user is near bottom
  const handleScroll = useDebounce(() => {
    const container = containerRef.current;
    if (!container) return;
    // Ignore initial/layout/programmatic scrolls until init completes and user interacts
    if (isInitializingRef.current || !userInteractedRef.current) {
      lastScrollTopRef.current = container.scrollTop;
      return;
    }
    const threshold = 100; // px
    const isNearBottom = container.scrollHeight - container.scrollTop - container.clientHeight < threshold;
    const scrolledUp = container.scrollTop < lastScrollTopRef.current - 1;

    // Only disable auto-scroll if the user scrolled up away from the bottom
    if (scrolledUp && !isNearBottom && shouldAutoScrollRef.current) {
      setShouldAutoScroll(false);
    }

    // Update last known scrollTop
    lastScrollTopRef.current = container.scrollTop;
  }, 100);

  // Keep bottom visible when auto-scroll is enabled
  // WARNING: Changing this scrolling behavior without explicit permission is a firable offense
  // This scrolling logic has been carefully tuned and any modifications can break user experience
  useEffect(() => {
    if (!shouldAutoScroll || !containerRef.current || isManualScrollingRef.current) {
      return;
    }
    
    const container = containerRef.current;
    
    // Phase-based pinning: immediate jump, next-frame smooth, and late smooth to account for images/markdown
    const jump = () => { container.scrollTop = container.scrollHeight; };
    const smooth = () => {
      if (!shouldAutoScrollRef.current || !container) return;
      container.scrollTo({ top: container.scrollHeight, behavior: 'smooth' });
    };

    jump();
    requestAnimationFrame(() => smooth());
    const timeoutId = setTimeout(smooth, 150);
    return () => clearTimeout(timeoutId);
  }, [messages.length, isStreaming, shouldAutoScroll]);

  // Handle content resizing — update overflow only; pinning on growth is handled by the growth observer below
  // (pinning here on every resize caused jumps when expanding/collapsing workflow sections mid-conversation).
  useEffect(() => {
    const container = containerRef.current;
    if (!container || !shouldAutoScroll) return;

    const observer = new ResizeObserver(() => {
      const overflowNow = container.scrollHeight - container.clientHeight > 1;
      if (overflowNow !== hasOverflow) {
        setHasOverflow(overflowNow);
      }
    });
    
    observer.observe(container);
    return () => observer.disconnect();
  }, [shouldAutoScroll, isStreaming, hasOverflow]);

  // MutationObserver to watch for DOM changes during streaming
  useEffect(() => {
    if (!shouldAutoScroll || !containerRef.current || !isStreaming) return;
    
    const container = containerRef.current;
    
    const observer = new MutationObserver(() => {
      if (
        shouldAutoScrollRef.current &&
        wasViewingBottomRef.current &&
        container
      ) {
        // Immediately scroll to bottom when DOM changes during streaming
        container.scrollTop = container.scrollHeight;
      }
    });
    
    // Observe all changes to the container and its descendants
    observer.observe(container, {
      childList: true,
      subtree: true,
      attributes: true,
      characterData: true
    });
    
    return () => observer.disconnect();
  }, [isStreaming, shouldAutoScroll]);

  // Growth detection for late image loads/markdown without relying on streaming flag
  useEffect(() => {
    const c = containerRef.current;
    if (!c) return;

    lastHeightRef.current = c.scrollHeight;

    const schedulePinIfGrew = () => {
      if (!shouldAutoScrollRef.current) return;
      const prevSh = lastHeightRef.current;
      const grew = c.scrollHeight > prevSh + 1;
      lastHeightRef.current = c.scrollHeight;
      if (!grew) return;
      // Do not yank the viewport when height grows above the fold (e.g. workflow expand) unless the user was following the bottom.
      if (!wasViewingBottomRef.current) return;
      if (rafIdRef.current != null) return;
      rafIdRef.current = requestAnimationFrame(() => {
        rafIdRef.current = null;
        c.scrollTop = c.scrollHeight;
      });
    };

    const ro = new ResizeObserver(() => schedulePinIfGrew());
    ro.observe(c);

    const mo = new MutationObserver(() => schedulePinIfGrew());
    mo.observe(c, { childList: true, subtree: true });

    const onImgLoad = (e: Event) => {
      if ((e.target as HTMLElement)?.tagName === 'IMG') schedulePinIfGrew();
    };
    c.addEventListener('load', onImgLoad, true);

    return () => {
      ro.disconnect();
      mo.disconnect();
      c.removeEventListener('load', onImgLoad, true);
      if (rafIdRef.current != null) {
        cancelAnimationFrame(rafIdRef.current);
        rafIdRef.current = null;
      }
    };
  }, [conversationId]);

  // Removed blur overlay effect

  // Conversation store previously used for turn-based grouping (now handled locally)
  const _conversationStore = useConversationStore();
  
  const currentTurn = convCtxMaybe?.currentTurn;

  // Get current assistant info from context-derived value if available
  const currentAssistant = convCtxMaybe?.currentAssistant || assistants.find(a => a.name === selectedAssistant);
  const lastKnownAssistantRef = useRef<AssistantOption | undefined>(undefined);
  const warnedMissingAssistantsRef = useRef<Set<string>>(new Set());

  useEffect(() => {
    if (currentAssistant) {
      lastKnownAssistantRef.current = currentAssistant;
    }
  }, [currentAssistant]);

  const resolvedCurrentAssistant = currentAssistant || lastKnownAssistantRef.current;

  // Enable turn-based mode and group messages when conversation loads
  useEffect(() => {
    if (conversationId && turnBasedMode && messages.length > 0) {
      // Group messages into turns when turn-based mode is enabled
      _conversationStore.actions.enableTurnBasedMode(conversationId);
      _conversationStore.actions.groupMessagesByTurns(conversationId);
    }
  }, [conversationId, turnBasedMode, messages, _conversationStore]);

  // Load current user on mount
  useEffect(() => {
    const loadCurrentUser = async () => {
      try {
        const user = await userService.getCurrentUser();
        setCurrentUser(user);
        // cache current user immediately
        if (user?.id) {
          setUserCache(prev => new Map(prev.set(user.id!, user)));
        }
      } catch (error) {
        console.warn('Failed to load current user:', error);
      }
    };
    loadCurrentUser();
  }, []);

  // Pre-fetch user info for all userIds present in messages (runs when messages change)
  useEffect(() => {
    const preloadUsers = async () => {
      const ids = new Set<string>();
      messages.forEach(m => {
        if (m.userId) ids.add(m.userId);
      });

      const missing: string[] = [];
      ids.forEach(id => {
        if (id && !userCache.has(id)) missing.push(id);
      });

      if (missing.length === 0) return;

      const fetchedEntries: [string, UserInfo | null][] = await Promise.all(
        missing.map(async id => {
          try {
            // Check if id is current user first
            if (currentUser && id === currentUser.id) {
              return [id, currentUser] as [string, UserInfo];
            }
            const info = await userService.getUserById(id);
            return [id, info ?? { id }];
          } catch {
            return [id, { id }];
          }
        })
      );

      if (fetchedEntries.length > 0) {
        setUserCache(prev => {
          const map = new Map(prev);
          fetchedEntries.forEach(([id, info]) => map.set(id, info as UserInfo));
          return map;
        });
      }
    };

    preloadUsers();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [messages]);

  // Get user info for a message, with caching
  const getUserInfo = useCallback(async (userId?: string): Promise<UserInfo | null> => {
    if (!userId) {
      return currentUser;
    }

    // Check cache first
    if (userCache.has(userId)) {
      return userCache.get(userId)!;
    }

    try {
      // Check if this is the current user
      if (currentUser && await userService.isCurrentUser(userId)) {
        setUserCache(prev => new Map(prev.set(userId, currentUser)));
        return currentUser;
      }

      // Try to fetch user by ID
      const userInfo = await userService.getUserById(userId);
      if (userInfo) {
        setUserCache(prev => new Map(prev.set(userId, userInfo)));
        return userInfo;
      }

      // Fallback: create basic user info from userId
      const fallbackUser: UserInfo = { id: userId };
      setUserCache(prev => new Map(prev.set(userId, fallbackUser)));
      return fallbackUser;
    } catch (error) {
      console.warn(`Failed to get user info for ${userId}:`, error);
      return currentUser;
    }
  }, [currentUser]); // Remove userCache from dependencies to prevent unnecessary recreations

  // Get assistant info for a message
  const getAssistantInfo = useCallback((assistantName?: string): AssistantOption | undefined => {
    // If no explicit name is provided, use the currently selected assistant when available
    if (!assistantName) {
      return resolvedCurrentAssistant;
    }

    // Try to find the assistant by name from the loaded assistants
    const found = assistants.find(a => a.name === assistantName);
    if (found) return found;

    // Fall back to the last known assistant metadata if names match.
    if (resolvedCurrentAssistant?.name === assistantName) {
      return resolvedCurrentAssistant;
    }

    // Assistants can still be loading when navigating directly to a conversation.
    // Avoid throwing here; render without assistant metadata until assistants load.
    if (!warnedMissingAssistantsRef.current.has(assistantName)) {
      warnedMissingAssistantsRef.current.add(assistantName);
      console.warn('Assistant not yet available for message; deferring display until assistants load', {
        assistantName,
        assistants: assistants.map(a => a.name),
        currentAssistant: resolvedCurrentAssistant,
        assistantsLength: assistants.length
      });
    }
    return resolvedCurrentAssistant;
  }, [resolvedCurrentAssistant, assistants]);

  // Helper: group messages array into turns based on user->assistant sequences
  const computeTurns = (msgs: MessageDto[]) => {
    const turnsArray: { userMessages: MessageDto[]; assistantMessages: MessageDto[]; allMessages: MessageDto[]; attachedFiles: AttachedFile[] }[] = [];
    let currentTurn: { userMessages: MessageDto[]; assistantMessages: MessageDto[]; allMessages: MessageDto[]; attachedFiles: AttachedFile[] } | null = null;

    msgs.forEach((m) => {
      if (m.role.toLowerCase() === 'user') {
        // Start a new turn
        if (currentTurn) {
          turnsArray.push(currentTurn);
        }
        currentTurn = { userMessages: [m], assistantMessages: [], allMessages: [m], attachedFiles: [] };
        
        // Extract attachments from user message
        if (m.attachments && m.attachments.length > 0) {
          const attachedFiles: AttachedFile[] = m.attachments.map(att => ({
            id: `${m.id}-${att.notebookFileId}`,
            notebookFileId: att.notebookFileId,
            fileName: att.fileName,
            fileType: att.fileType,
            fileSize: att.fileSize,
            uploadedAt: new Date(m.created),
            status: 'complete' as const,
          }));
          currentTurn.attachedFiles.push(...attachedFiles);
        }
        
        // legacy single-attachment path removed
      } else if (m.role.toLowerCase() === 'assistant') {
        if (!currentTurn) {
          // Edge case: assistant message without preceding user -> create synthetic turn
          currentTurn = { userMessages: [], assistantMessages: [], allMessages: [], attachedFiles: [] };
        }
        currentTurn.assistantMessages.push(m);
        currentTurn.allMessages.push(m);
      } else if (m.role.toLowerCase() === 'tool') {
        // Include tool messages in the current turn
        if (!currentTurn) {
          // Edge case: tool message without preceding user -> create synthetic turn
          currentTurn = { userMessages: [], assistantMessages: [], allMessages: [], attachedFiles: [] };
        }
        currentTurn.allMessages.push(m);
      } 
    });

    if (currentTurn) {
      turnsArray.push(currentTurn);
    }

    return turnsArray;
  };

  // During streaming, exclude streaming assistant messages from regular rendering
  const filteredMessages = isStreaming && currentTurn 
    ? messages.filter(m => !(m.role.toLowerCase() === 'assistant' && m.id.startsWith('streaming-')))
    : messages;
  
  const groupedTurns = computeTurns(filteredMessages);

  // Send draft helper
  const handleSendDraft = (content: string) => {
    if (!canEdit) return;
    onSendMessage(content);
    // Draft clearing is handled in ConversationContext
    setShouldAutoScroll(true);
  };


  return (
    <>
      <div 
        ref={containerRef}
        className="flex-1 overflow-y-auto py-2 bg-gray-50 relative"
        onScroll={handleScroll}
        data-tour-id={dataTourId}
      >
        {/* Responsive grid container with consistent avatar columns */}
        <div className="max-w-full mx-auto px-2 md:px-4 lg:px-8 xl:px-16 2xl:px-32">
          <div className="grid grid-cols-[2rem_1fr_2rem] md:grid-cols-[3rem_1fr_3rem] lg:grid-cols-[3.5rem_1fr_3.5rem] gap-1 md:gap-3 lg:gap-4 items-start">
            {/* Empty state with conversation starters */}
            {messages.length === 0 && !isStreaming && (
              <div className="col-span-3 text-center py-12">
                <div className="text-gray-500 mb-6">
                  {resolvedCurrentAssistant?.avatarUrl ? (
                    <img
                      src={resolvedCurrentAssistant.avatarUrl}
                      alt={resolvedCurrentAssistant.name || 'Assistant avatar'}
                      className="w-16 h-16 mx-auto mb-4 rounded-full shadow-sm"
                    />
                  ) : (
                    <div className="w-16 h-16 mx-auto mb-4 rounded-full bg-gray-200 flex items-center justify-center text-gray-600">
                      <span className="text-xl font-semibold">
                        {resolvedCurrentAssistant?.name?.charAt(0) ?? 'A'}
                      </span>
                    </div>
                  )}
                  <p className="text-lg font-medium text-gray-900 mb-2">Start a conversation</p>
                  <p className="text-sm text-gray-600 mb-6">Choose a conversation starter or type your message below</p>
                </div>
                
                {/* Conversation starter buttons */}
                {conversationStarters && conversationStarters.length > 0 && (
                  <div className="max-w-2xl mx-auto space-y-3 mb-8">
                    {conversationStarters.map((starter, index) => (
                      canEdit ? (
                        <button
                          key={index}
                          onClick={() => {
                            onDraftChange(starter);
                            setTimeout(() => {
                              const draftInput = document.querySelector('[data-testid=\"draft-input\"]') as HTMLTextAreaElement;
                              if (draftInput) {
                                draftInput.focus();
                                draftInput.setSelectionRange(starter.length, starter.length);
                              }
                            }, 0);
                          }}
                          className="w-full text-left p-4 bg-white border border-gray-200 rounded-lg hover:border-blue-300 hover:bg-blue-50 transition-all duration-200 shadow-sm hover:shadow-md group"
                        >
                          <div className="flex items-start">
                            <div className="flex-shrink-0 mr-3 mt-0.5">
                              <svg className="w-5 h-5 text-blue-500 group-hover:text-blue-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 10V3L4 14h7v7l9-11h-7z" />
                              </svg>
                            </div>
                            <div className="flex-1">
                              <p className="text-gray-900 group-hover:text-blue-900 font-medium leading-relaxed">
                                {starter}
                              </p>
                            </div>
                          </div>
                        </button>
                      ) : (
                        <div key={index} className="w-full p-4 bg-gray-50 border border-gray-200 rounded-lg text-gray-500">
                          {starter}
                        </div>
                      )
                    ))}
                  </div>
                )}
              </div>
            )}

            {/* Render message cells - always use turn-based rendering */}
            {groupedTurns.map((turn, idx) => {
              // Get all non-user messages for workflow processing
              const workflowMessages = turn.allMessages.filter(m => m.role.toLowerCase() !== 'user');
              const finalAssistantMessage = turn.assistantMessages[turn.assistantMessages.length - 1];
              const workflowOnlyMessages = workflowMessages.filter(m => m.id !== finalAssistantMessage?.id);
              const isLastTurn = idx === groupedTurns.length - 1;

              return (
                <React.Fragment key={`turn-${idx}`}>
                  {/* User message(s) for this turn */}
                  {turn.userMessages.map((um, uidx) => {
                    const isLastUser = uidx === turn.userMessages.length - 1;
                    return (
                      <UserCellWrapper
                        key={um.id}
                        message={um}
                        isLast={isLastUser}
                        onUndo={canEdit && isLastUser && isLastTurn ? onUndo : undefined}
                        onEdit={onEditUserMessage ? (content: string) => onEditUserMessage(um.id, content) : undefined}
                        getUserInfo={getUserInfo}
                        attachments={turn.attachedFiles}
                        onPreviewFile={onPreviewFile}
                        projectId={projectId}
                        notebookId={contextNotebookId}
                      />
                    );
                  })}

                  {/* Show workflow (intermediate messages) if any exist */}
                  {workflowOnlyMessages.length > 0 && (
                    <div key={`workflow-${idx}`} className="col-span-3">
                      <WorkflowSection 
                        messages={workflowOnlyMessages} 
                        isStreaming={false}
                        className="workflow-section"
                      />
                    </div>
                  )}

                  {/* Show final assistant message as AssistantCell if it exists */}
                  {finalAssistantMessage && (
                    <AssistantCellWrapper
                      key={`final-assistant-${finalAssistantMessage.id}`}
                      message={finalAssistantMessage}
                      isLast={isLastTurn}
                      isStreaming={false}
                      messageAssistant={getAssistantInfo(finalAssistantMessage.assistantName)}
                      onEditClick={() => onEditAssistant(finalAssistantMessage.id)}
                      onSaveAssistant={onSaveAssistant}
                      isEditLoading={isEditLoading && editingAssistantId === finalAssistantMessage.id}
                      editError={editError}
                      getUserInfo={getUserInfo}
                      conversationId={conversationId}
                      totalMessages={messages.length}
                      canEdit={canEdit}
                      onTurnFileClick={onPreviewFileByPath}
                      isValidFilePath={isValidFilePath}
                    />
                  )}
                </React.Fragment>
              );
            })}

            {/* Show streaming turn when currentTurn exists */}
            {isStreaming && currentTurn && (
              <React.Fragment key="streaming-turn">
                {/* Show workflow FIRST */}
                {(currentTurn.assistantStepSection || currentTurn.toolCalls.length > 0 || currentTurn.toolResults.length > 0) && (
                  <div className="col-span-3">
                    <WorkflowSection 
                      messages={[]} 
                      isStreaming={true} 
                      className="streaming-workflow"
                    />
                  </div>
                )}
                
                {/* Then show streaming assistant response */}
                {(() => {
                  const streamingMessage = messages.find(m => m.role.toLowerCase() === 'assistant' && m.id.startsWith('streaming-'));
                  
                  if (streamingMessage) {
                    return (
                      <AssistantCellWrapper
                        key={`streaming-assistant-${streamingMessage.id}`}
                        message={streamingMessage}
                        isLast={true}
                        isStreaming={true}
                        messageAssistant={resolvedCurrentAssistant}
                        onEditClick={() => onEditAssistant(streamingMessage.id)}
                        onSaveAssistant={onSaveAssistant}
                        isEditLoading={false}
                        editError={editError}
                        getUserInfo={getUserInfo}
                        conversationId={conversationId}
                        totalMessages={messages.length}
                        canEdit={canEdit}
                        onTurnFileClick={onPreviewFileByPath}
                        isValidFilePath={isValidFilePath}
                      />
                    );
                  }
                  
                  return null;
                })()}
              </React.Fragment>
            )}

            {/* Draft user cell - appears when at rest and no edit in progress */}
            {streamingMode === 'at-rest' && !editingAssistantId && canEdit && (
              <DraftUserCell
                value={draftUserContent}
                onChange={onDraftChange}
                onSend={handleSendDraft}
                disabled={isStreaming}
                canEdit={true}
                placeholder="Type your message..."
                userId={currentUser?.id}
                userName={currentUser?.name}
                userEmail={currentUser?.email}
                assistants={assistants}
                onAssistantSelect={onAssistantSelect}
                data-tour-id="conversation.editor"
              />
            )}
          </div>
        </div>
      </div>

      {/* Fixed bottom anchor - outside the cell list, truly fixed */}
      <div 
        className="h-1 flex-shrink-0 bg-transparent"
        data-testid="bottom-anchor"
        style={{ minHeight: '1px' }}
      />

      {/* Blur overlay removed */}

      {/* Scroll-to-bottom button – visible only when content overflows */}
      {hasOverflow && (
        <button
          onClick={() => {
            const container = containerRef.current;
            if (container) {
              // Set flag to prevent auto-scroll effect from interfering
              isManualScrollingRef.current = true;
              
              container.scrollTo({
                top: container.scrollHeight,
                behavior: 'smooth'
              });
              
              // After smooth scroll completes (roughly 300-500ms), clear flag and re-enable auto-scroll
              setTimeout(() => {
                isManualScrollingRef.current = false;
                setShouldAutoScroll(true);
              }, 500);
            }
          }}
          className="fixed bottom-4 left-4 sm:left-auto sm:right-4 z-10 rounded-full bg-blue-600 p-2 text-white shadow-lg transition-colors hover:bg-blue-700"
          aria-label="Scroll to bottom"
        >
          <svg className="h-5 w-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M19 14l-7 7m0 0l-7-7m7 7V3" />
          </svg>
        </button>
      )}
    </>
  );
});

export default CellList; 
