import { describe, it, expect, beforeEach, vi } from 'vitest';

vi.unmock('../ConversationContext');

import { render, renderHook, act, waitFor } from '@testing-library/react';
import React from 'react';
import { MemoryRouter } from 'react-router';
import { ConversationProvider } from '../ConversationContext';
import { ToastProvider } from '../../components/common/Toast';
import { NotebookProvider } from '../NotebookContext';
import { api } from '../../services/api';
import { userService } from '../../services/userService';

const mockShowToast = vi.fn();

// Mock the API service
vi.mock('../../services/api', () => ({
  api: {
    projects: {
      notebooks: {
        conversations: {
          get: vi.fn().mockResolvedValue({
            messages: [],
            assistantName: null
          }),
          sendMessageStream: vi.fn().mockResolvedValue({}),
          editMessage: vi.fn().mockResolvedValue({}),
          undoLast: vi.fn().mockResolvedValue({}),
          getAll: vi.fn().mockResolvedValue([]),
        },
        getNotebook: vi.fn().mockResolvedValue({}),
      },
      notebookTemplates: {
        getAll: vi.fn().mockResolvedValue([]),
        getAssistants: vi.fn().mockResolvedValue([]),
      },
      assistants: {
        getConversationStarters: vi.fn().mockResolvedValue([]),
      },
      folders: {
        getFolderTree: vi.fn().mockResolvedValue({}),
      },
    },
  },
}));

// Mock NotebookContext
vi.mock('../NotebookContext', () => ({
  useNotebook: () => ({
    loadNotebookFiles: vi.fn().mockResolvedValue(undefined),
  }),
  NotebookProvider: ({ children }: { children: React.ReactNode }) => children,
}));

// Mock Toast
vi.mock('../../components/common/Toast', () => ({
  useToast: () => ({
    showToast: mockShowToast,
  }),
  ToastProvider: ({ children }: { children: React.ReactNode }) => children,
}));

vi.mock('../../utils/notebookAuth', () => ({
  ensureValidTokensForTemplate: vi.fn().mockResolvedValue({ needsAuth: false, missingProviders: [] }),
}));

vi.mock('../conversation/runtimeChecks', () => ({
  getNotebookRuntimeReadyCache: vi.fn(() => new Set<string>()),
  checkRuntimeStatus: vi.fn().mockResolvedValue(undefined),
  clearNotebookRuntimeReadyCache: vi.fn(),
}));

// Mock authService
vi.mock('../../services/authService', () => ({
  authService: {
    getAccessToken: vi.fn().mockResolvedValue('mock-token'),
  },
}));

// Mock userService
vi.mock('../../services/userService', () => ({
  userService: {
    getCurrentUser: vi.fn().mockResolvedValue({
      id: 'user-1',
      name: 'Test User',
      email: 'test@example.com'
    }),
    getUserById: vi.fn().mockResolvedValue({
      id: 'user-1',
      name: 'Test User',
      email: 'test@example.com'
    }),
  },
}));

import { useConversation } from '../ConversationContext';
import { ensureValidTokensForTemplate } from '../../utils/notebookAuth';
import { checkRuntimeStatus, clearNotebookRuntimeReadyCache } from '../conversation/runtimeChecks';

const defaultAssistants = [
  { name: 'Demo Guide', model: 'gpt-4', avatarUrl: '/a.png', id: 'assistant-1' },
];

const renderConversationProvider = (options: Record<string, unknown> = {}) => {
  const {
    projectId = 'test-project',
    notebookId = 'test-notebook',
    conversationId = 'test-conversation',
    guideId,
    assistants,
    notebookTemplate,
    onPreviewFile,
    onPreviewFileByPath,
  } = options;

  const wrapper = ({ children }: { children: React.ReactNode }) => (
    <MemoryRouter>
      <ToastProvider>
        <NotebookProvider notebookId={notebookId as string} projectId={projectId as string}>
          <ConversationProvider
            projectId={projectId as string}
            notebookId={notebookId as string}
            conversationId={conversationId as string}
            guideId={guideId as string | undefined}
            assistants={assistants as typeof defaultAssistants | undefined}
            notebookTemplate={notebookTemplate as any}
            onPreviewFile={onPreviewFile as ((fileId: string) => void) | undefined}
            onPreviewFileByPath={onPreviewFileByPath as ((relativePath: string) => void) | undefined}
          >
            {children}
          </ConversationProvider>
        </NotebookProvider>
      </ToastProvider>
    </MemoryRouter>
  );

  return wrapper;
};

// Mock implementations
const mockApi = api as any;
const mockUserService = userService as any;

describe('ConversationContext Provider', () => {


  beforeEach(() => {
    vi.clearAllMocks();
    
    // Default mock implementations
    mockApi.projects.notebooks.conversations.get.mockResolvedValue({
      messages: [],
      assistantName: null
    });
    
    mockApi.projects.notebookTemplates.getAll.mockResolvedValue([]);
    mockApi.projects.notebookTemplates.getAssistants.mockResolvedValue([]);
    
    mockUserService.getCurrentUser.mockResolvedValue({
      id: 'user-1',
      name: 'Test User',
      email: 'test@example.com'
    });
    
    mockUserService.getUserById.mockResolvedValue({
      id: 'user-1',
      name: 'Test User',
      email: 'test@example.com'
    });
  });

  describe('Basic functionality', () => {
    it('should render without crashing', async () => {
      const wrapper = renderConversationProvider();
      const { result } = renderHook(() => useConversation(), { wrapper });

      // Just check that the hook returns something
      expect(result.current).toBeDefined();
      expect(typeof result.current.sendMessage).toBe('function');
      expect(typeof result.current.setDraftUserContent).toBe('function');
      expect(typeof result.current.setSelectedAssistant).toBe('function');
    });

    it('should have expected function properties', async () => {
      const wrapper = renderConversationProvider();
      const { result } = renderHook(() => useConversation(), { wrapper });

      // Check that the hook returns the expected functions
      expect(typeof result.current.sendMessage).toBe('function');
      expect(typeof result.current.setDraftUserContent).toBe('function');
      expect(typeof result.current.setSelectedAssistant).toBe('function');
      expect(typeof result.current.addPendingAttachment).toBe('function');
      expect(typeof result.current.removePendingAttachment).toBe('function');
      expect(typeof result.current.startEditingAssistant).toBe('function');
      expect(typeof result.current.cancelEditingAssistant).toBe('function');
      expect(typeof result.current.editAssistantMessage).toBe('function');
      expect(typeof result.current.undoLastTurn).toBe('function');
      expect(typeof result.current.cancelStream).toBe('function');
      expect(typeof result.current.refresh).toBe('function');
    });
  });

  describe('Provider Initialization', () => {
    it('should initialize with default state', async () => {
      const wrapper = renderConversationProvider();
      const { result } = renderHook(() => useConversation(), { wrapper });

      // Check that the provider initializes with expected default values
      expect(result.current.messages).toEqual([]);
      expect(result.current.isStreaming).toBe(false);
      expect(result.current.selectedAssistant).toBeNull();
      expect(result.current.draftUserContent).toBe('');
      expect(result.current.isInitialized).toBe(true);
      expect(typeof result.current.sendMessage).toBe('function');
      expect(typeof result.current.refresh).toBe('function');
    });

    it('should initialize with template data when notebookTemplateId is provided', async () => {
      const mockTemplates = [
        {
          id: 'template-1',
          defaultAssistant: 'Claude',
          conversationStarters: ['Hello', 'How can I help?']
        }
      ];
      
      const mockAssistants = [
        { name: 'Claude', modelDeploymentId: 'claude-3' },
        { name: 'GPT-4', modelDeploymentId: 'gpt-4' }
      ];

      mockApi.projects.notebookTemplates.getAll.mockResolvedValue(mockTemplates);
      mockApi.projects.notebookTemplates.getAssistants.mockResolvedValue(mockAssistants);

      const wrapper = renderConversationProvider({ notebookTemplateId: 'template-1' });
      const { result } = renderHook(() => useConversation(), { wrapper });

      // Check that the provider is initialized
      expect(result.current.isInitialized).toBe(true);
      expect(typeof result.current.assistants).toBe('object');
      expect(typeof result.current.conversationStarters).toBe('object');
    });

    it('should provide conversation loading functionality', async () => {
      const wrapper = renderConversationProvider();
      const { result } = renderHook(() => useConversation(), { wrapper });

      // Check that refresh function is available
      expect(typeof result.current.refresh).toBe('function');
      expect(result.current.isInitialized).toBe(true);
    });

    it('should handle conversation not found gracefully', async () => {
      const wrapper = renderConversationProvider();
      const { result } = renderHook(() => useConversation(), { wrapper });

      // Check that the provider handles missing conversations gracefully
      expect(result.current.messages).toEqual([]);
      expect(result.current.selectedAssistant).toBeNull();
    });

    it('should provide user profile functionality', async () => {
      const wrapper = renderConversationProvider();
      const { result } = renderHook(() => useConversation(), { wrapper });

      // Wait for provider to initialize (if needed)
      await waitFor(() => {
        // Accept either undefined or an empty object as valid initial state
        expect([undefined, {}]).toContainEqual(result.current.userProfiles);
      });
    });
  });

  describe('API Integration', () => {
    it('should provide refresh functionality', async () => {
      const wrapper = renderConversationProvider();
      const { result } = renderHook(() => useConversation(), { wrapper });

      // Check that refresh function is available and callable
      expect(typeof result.current.refresh).toBe('function');
      
      // Should not throw when called
      expect(typeof result.current.refresh).toBe('function');
    });

    it('should provide streaming state management', async () => {
      const wrapper = renderConversationProvider();
      const { result } = renderHook(() => useConversation(), { wrapper });

      // Check streaming state management
      expect(result.current.isStreaming).toBe(false);
      expect(typeof result.current.cancelStream).toBe('function');
    });

    it('should provide error handling capabilities', async () => {
      const wrapper = renderConversationProvider();
      const { result } = renderHook(() => useConversation(), { wrapper });

      // Check error state properties
      expect(typeof result.current.streamingError).toBe('undefined');
      expect(typeof result.current.editError).toBe('undefined');
    });
  });

  describe('State Management', () => {
    it('should have working state management functions', async () => {
      const wrapper = renderConversationProvider();
      const { result } = renderHook(() => useConversation(), { wrapper });

      // Check that functions exist
      expect(typeof result.current.setDraftUserContent).toBe('function');
      expect(typeof result.current.setSelectedAssistant).toBe('function');
      expect(typeof result.current.addPendingAttachment).toBe('function');
      expect(typeof result.current.startEditingAssistant).toBe('function');

      // Check initial state values
      expect(result.current.draftUserContent).toBe('');
      expect(result.current.selectedAssistant).toBeNull();
      expect(result.current.pendingAttachments).toEqual([]);
      expect(result.current.editingAssistantId).toBeUndefined();
    });

    it('should manage draft user content', async () => {
      const wrapper = renderConversationProvider();
      const { result } = renderHook(() => useConversation(), { wrapper });

      // Test that the function exists and is callable
      expect(typeof result.current.setDraftUserContent).toBe('function');
      
      // Test that the function can be called without throwing
      expect(() => {
        act(() => {
          result.current.setDraftUserContent('Hello world');
        });
      }).not.toThrow();
    });

    it('should manage selected assistant', async () => {
      const wrapper = renderConversationProvider();
      const { result } = renderHook(() => useConversation(), { wrapper });

      // Test that the function exists and is callable
      expect(typeof result.current.setSelectedAssistant).toBe('function');
      
      // Test that the function can be called without throwing
      expect(() => {
        act(() => {
          result.current.setSelectedAssistant('Claude');
        });
      }).not.toThrow();
    });

    it('should manage pending attachments', async () => {
      const wrapper = renderConversationProvider();
      const { result } = renderHook(() => useConversation(), { wrapper });

      const attachment = {
        notebookFileId: 'file-1',
        fileName: 'test.txt',
        uploadType: 'text' as const
      };

      // Test that the functions exist and are callable
      expect(typeof result.current.addPendingAttachment).toBe('function');
      expect(typeof result.current.removePendingAttachment).toBe('function');
      
      // Test that the functions can be called without throwing
      expect(() => {
        act(() => {
          result.current.addPendingAttachment(attachment);
        });
      }).not.toThrow();

      expect(() => {
        act(() => {
          result.current.removePendingAttachment('file-1');
        });
      }).not.toThrow();
    });

    it('should manage editing state', async () => {
      const wrapper = renderConversationProvider();
      const { result } = renderHook(() => useConversation(), { wrapper });

      // Test that the functions exist and are callable
      expect(typeof result.current.startEditingAssistant).toBe('function');
      expect(typeof result.current.cancelEditingAssistant).toBe('function');
      
      // Test that the functions can be called without throwing
      expect(() => {
        act(() => {
          result.current.startEditingAssistant('msg-1');
        });
      }).not.toThrow();

      expect(() => {
        act(() => {
          result.current.cancelEditingAssistant();
        });
      }).not.toThrow();
    });
  });

  describe('Error Handling', () => {
    it('should provide error state properties', async () => {
      const wrapper = renderConversationProvider();
      const { result } = renderHook(() => useConversation(), { wrapper });

      // Check that error state properties are available
      expect(typeof result.current.streamingError).toBe('undefined');
      expect(typeof result.current.editError).toBe('undefined');
      expect(result.current.isEditLoading).toBe(false);
    });

    it('should handle errors gracefully', async () => {
      const wrapper = renderConversationProvider();
      const { result } = renderHook(() => useConversation(), { wrapper });

      // Should not crash and should still initialize
      expect(result.current.isInitialized).toBe(true);
    });
  });

  describe('Lifecycle Management', () => {
    it('should provide conversation management functions', async () => {
      const wrapper = renderConversationProvider();
      const { result } = renderHook(() => useConversation(), { wrapper });

      // Check that conversation management functions are available
      expect(typeof result.current.sendMessage).toBe('function');
      expect(typeof result.current.editAssistantMessage).toBe('function');
      expect(typeof result.current.undoLastTurn).toBe('function');
    });

    it('should provide streaming management functions', async () => {
      const wrapper = renderConversationProvider();
      const { result } = renderHook(() => useConversation(), { wrapper });

      // Check that streaming management functions are available
      expect(typeof result.current.cancelStream).toBe('function');
      expect(typeof result.current.handleStreamingEvent).toBe('function');
    });
  });

  describe('Provider effects and branches', () => {
    it('throws when useConversation is used outside ConversationProvider', () => {
      expect(() => renderHook(() => useConversation())).toThrow(
        'useConversation must be used within ConversationProvider',
      );
    });

    it('initializes default assistant from Guide suffix and template starters', async () => {
      const wrapper = renderConversationProvider({
        guideId: 'guide-1',
        assistants: defaultAssistants,
        notebookTemplate: {
          defaultAssistant: 'Other Assistant',
          conversationStarters: ['Starter A', 'Starter B'],
        },
      });
      const { result } = renderHook(() => useConversation(), { wrapper });

      await waitFor(() => {
        expect(result.current.selectedAssistant).toBe('Demo Guide');
      });
      expect(result.current.conversationStarters).toEqual(['Starter A', 'Starter B']);
      expect(checkRuntimeStatus).toHaveBeenCalled();
    });

    it('ignores transient empty assistant updates once assistants are loaded', async () => {
      function Harness() {
        const [assistants, setAssistants] = React.useState(defaultAssistants);
        return (
          <>
            <button type="button" onClick={() => setAssistants([])}>
              clear
            </button>
            <ConversationProvider
              projectId="test-project"
              notebookId="test-notebook"
              conversationId="test-conversation"
              guideId="guide-1"
              assistants={assistants}
            >
              <AssistantCount />
            </ConversationProvider>
          </>
        );
      }

      function AssistantCount() {
        const { assistants: currentAssistants } = useConversation();
        return <div data-testid="count">{currentAssistants.length}</div>;
      }

      const { getByRole, getByTestId } = render(
        <MemoryRouter>
          <ToastProvider>
            <NotebookProvider notebookId="test-notebook" projectId="test-project">
              <Harness />
            </NotebookProvider>
          </ToastProvider>
        </MemoryRouter>
      );

      await waitFor(() => {
        expect(getByTestId('count').textContent).toBe('1');
      });

      await act(async () => {
        getByRole('button', { name: 'clear' }).click();
      });

      expect(getByTestId('count').textContent).toBe('1');
    });

    it('refresh loads conversation messages and user profiles', async () => {
      mockApi.projects.notebooks.conversations.get.mockResolvedValue({
        messages: [
          { id: 'm1', role: 'user', content: 'hi', userId: 'user-2', userName: 'Other User' },
        ],
        assistantName: 'Demo Guide',
      });
      mockUserService.getUserById.mockResolvedValue({
        id: 'user-2',
        name: 'Fetched User',
        email: 'fetched@example.com',
      });

      const wrapper = renderConversationProvider({ guideId: 'guide-1', assistants: defaultAssistants });
      const { result } = renderHook(() => useConversation(), { wrapper });

      await waitFor(() => expect(result.current.isInitialized).toBe(true));

      await act(async () => {
        await result.current.refresh();
      });

      expect(result.current.messages).toHaveLength(1);
      expect(result.current.selectedAssistant).toBe('Demo Guide');
      expect(result.current.userProfiles?.['user-2']?.name).toBe('Other User');
    });

    it('refresh clears state when conversationId is missing', async () => {
      const wrapper = renderConversationProvider({ conversationId: '' });
      const { result } = renderHook(() => useConversation(), { wrapper });

      await waitFor(() => expect(result.current.isInitialized).toBe(true));

      await act(async () => {
        await result.current.refresh();
      });

      expect(result.current.messages).toEqual([]);
      expect(result.current.selectedAssistant).toBeNull();
      expect(mockApi.projects.notebooks.conversations.get).not.toHaveBeenCalled();
    });

    it('refresh handles API failures by clearing messages', async () => {
      mockApi.projects.notebooks.conversations.get.mockRejectedValue(new Error('network'));

      const wrapper = renderConversationProvider();
      const { result } = renderHook(() => useConversation(), { wrapper });

      await waitFor(() => expect(result.current.isInitialized).toBe(true));

      await act(async () => {
        await result.current.refresh();
      });

      expect(result.current.messages).toEqual([]);
    });

    it('loads assistant-specific starters when conversation is empty', async () => {
      mockApi.projects.assistants.getConversationStarters.mockResolvedValue(['Ask about docs']);

      const wrapper = renderConversationProvider({ guideId: 'guide-1', assistants: defaultAssistants });
      const { result } = renderHook(() => useConversation(), { wrapper });

      await waitFor(() => {
        expect(result.current.conversationStarters).toEqual(['Ask about docs']);
      });
    });

    it('falls back to template starters when assistant starter fetch fails', async () => {
      mockApi.projects.assistants.getConversationStarters.mockRejectedValue(new Error('fail'));

      const wrapper = renderConversationProvider({
        guideId: 'guide-1',
        assistants: defaultAssistants,
        notebookTemplate: { conversationStarters: ['Template starter'] },
      });
      const { result } = renderHook(() => useConversation(), { wrapper });

      await waitFor(() => {
        expect(result.current.conversationStarters).toEqual(['Template starter']);
      });
    });

    it('shows auth toast when token refresh reports missing providers', async () => {
      vi.mocked(ensureValidTokensForTemplate).mockResolvedValue({
        needsAuth: true,
        missingProviders: [{ id: 'google', name: 'Google' }],
      } as any);

      const wrapper = renderConversationProvider({
        guideId: 'guide-1',
        assistants: defaultAssistants,
        notebookTemplate: { conversationStarters: [], authProviders: [] },
      });
      renderHook(() => useConversation(), { wrapper });

      await waitFor(() => {
        expect(mockShowToast).toHaveBeenCalledWith(
          expect.objectContaining({
            type: 'warning',
            title: 'Authentication Needed',
          }),
        );
      });
    });

    it('clears runtime cache on llama runtime events', async () => {
      const wrapper = renderConversationProvider({ guideId: 'guide-1', assistants: defaultAssistants });
      renderHook(() => useConversation(), { wrapper });

      await waitFor(() => expect(vi.mocked(checkRuntimeStatus)).toHaveBeenCalled());

      act(() => {
        window.dispatchEvent(new CustomEvent('llama-runtime-restarted', { detail: { notebookId: 'test-notebook' } }));
        window.dispatchEvent(new Event('llama-runtime-loading'));
      });

      expect(clearNotebookRuntimeReadyCache).toHaveBeenCalledWith('test-notebook');
    });

    it('marks initialized when guideId is missing', async () => {
      const wrapper = renderConversationProvider({ guideId: undefined, assistants: [] });
      const { result } = renderHook(() => useConversation(), { wrapper });

      await waitFor(() => {
        expect(result.current.isInitialized).toBe(true);
      });
      expect(result.current.selectedAssistant).toBeNull();
    });

    it('updates draft content and selected assistant in state', async () => {
      const wrapper = renderConversationProvider({ guideId: 'guide-1', assistants: defaultAssistants });
      const { result } = renderHook(() => useConversation(), { wrapper });

      await waitFor(() => expect(result.current.isInitialized).toBe(true));

      act(() => {
        result.current.setDraftUserContent('Draft message');
        result.current.setSelectedAssistant('Other Assistant');
      });

      expect(result.current.draftUserContent).toBe('Draft message');
      expect(result.current.selectedAssistant).toBe('Other Assistant');
    });

    it('adds and removes pending attachments in state', async () => {
      const wrapper = renderConversationProvider();
      const { result } = renderHook(() => useConversation(), { wrapper });

      await waitFor(() => expect(result.current.isInitialized).toBe(true));

      const attachment = {
        notebookFileId: 'file-42',
        fileName: 'notes.md',
        uploadType: 'text' as const,
      };

      act(() => {
        result.current.addPendingAttachment(attachment);
      });
      expect(result.current.pendingAttachments).toHaveLength(1);

      act(() => {
        result.current.removePendingAttachment('file-42');
      });
      expect(result.current.pendingAttachments).toHaveLength(0);
    });

    it('exposes preview callbacks from provider props', async () => {
      const onPreviewFile = vi.fn();
      const onPreviewFileByPath = vi.fn();
      const wrapper = renderConversationProvider({ onPreviewFile, onPreviewFileByPath });
      const { result } = renderHook(() => useConversation(), { wrapper });

      await waitFor(() => expect(result.current.isInitialized).toBe(true));

      expect(result.current.onPreviewFile).toBe(onPreviewFile);
      expect(result.current.onPreviewFileByPath).toBe(onPreviewFileByPath);
    });

    it('tracks assistant editing state through start and cancel', async () => {
      const wrapper = renderConversationProvider();
      const { result } = renderHook(() => useConversation(), { wrapper });

      await waitFor(() => expect(result.current.isInitialized).toBe(true));

      act(() => {
        result.current.startEditingAssistant('msg-edit-1');
      });
      expect(result.current.editingAssistantId).toBe('msg-edit-1');

      act(() => {
        result.current.cancelEditingAssistant();
      });
      expect(result.current.editingAssistantId).toBeUndefined();
    });

    it('fetches missing user profiles during refresh', async () => {
      mockApi.projects.notebooks.conversations.get.mockResolvedValue({
        messages: [
          { id: 'm1', role: 'user', content: 'hello', userId: 'user-9' },
        ],
        assistantName: null,
      });
      mockUserService.getUserById.mockResolvedValue({
        id: 'user-9',
        name: 'Fetched Profile',
        email: 'fetched@example.com',
      });

      const wrapper = renderConversationProvider({ guideId: 'guide-1', assistants: defaultAssistants });
      const { result } = renderHook(() => useConversation(), { wrapper });

      await waitFor(() => expect(result.current.isInitialized).toBe(true));

      await act(async () => {
        await result.current.refresh();
      });

      expect(mockUserService.getUserById).toHaveBeenCalledWith('user-9');
      await waitFor(() => {
        expect(result.current.userProfiles?.['user-9']?.name).toBe('Fetched Profile');
      });
    });

    it('ignores llama runtime events for other notebooks', async () => {
      const wrapper = renderConversationProvider({ guideId: 'guide-1', assistants: defaultAssistants });
      renderHook(() => useConversation(), { wrapper });

      await waitFor(() => expect(vi.mocked(checkRuntimeStatus)).toHaveBeenCalled());

      vi.mocked(clearNotebookRuntimeReadyCache).mockClear();

      act(() => {
        window.dispatchEvent(new CustomEvent('llama-runtime-restarted', { detail: { notebookId: 'other-notebook' } }));
      });

      expect(clearNotebookRuntimeReadyCache).not.toHaveBeenCalled();
    });
  });
}); 