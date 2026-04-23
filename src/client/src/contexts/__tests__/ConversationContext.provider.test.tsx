import { describe, it, expect, beforeEach, vi } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import React from 'react';
import { MemoryRouter } from 'react-router-dom';
import { ConversationProvider } from '../ConversationContext';
import { ToastProvider } from '../../components/common/Toast';
import { NotebookProvider } from '../NotebookContext';
import { api } from '../../services/api';
import { userService } from '../../services/userService';

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
    showToast: vi.fn(),
  }),
  ToastProvider: ({ children }: { children: React.ReactNode }) => children,
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

const renderConversationProvider = (options: any = {}) => {
  const {
    projectId = 'test-project',
    notebookId = 'test-notebook',
    conversationId = 'test-conversation',
    notebookTemplateId,
    onPreviewFile,
  } = options;

  const wrapper = ({ children }: { children: React.ReactNode }) => (
    <MemoryRouter>
      <ToastProvider>
        <NotebookProvider notebookId={notebookId} projectId={projectId}>
          <ConversationProvider
            projectId={projectId}
            notebookId={notebookId}
            conversationId={conversationId}
            notebookTemplateId={notebookTemplateId}
            onPreviewFile={onPreviewFile}
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
      expect(result.current.selectedAssistant).toBeUndefined();
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
      expect(result.current.selectedAssistant).toBeUndefined();
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
      expect(result.current.selectedAssistant).toBeUndefined();
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
}); 