import '@testing-library/jest-dom';
import { vi, beforeAll, afterAll, afterEach } from 'vitest';
import { cleanup } from '@testing-library/react';

// Clean up after each test
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

// Provide a default same-origin API URL for modules that resolve API_BASE_URL at import time.
window.__RUNTIME_CONFIG__ = { apiUrl: '/api' };

// Mock MSAL
vi.mock('@azure/msal-react', () => ({
  useMsal: vi.fn(() => ({
    instance: {
      handleRedirectPromise: vi.fn().mockResolvedValue(null),
      loginRedirect: vi.fn().mockResolvedValue(null),
      logoutRedirect: vi.fn().mockResolvedValue(null),
      getAllAccounts: vi.fn().mockReturnValue([]),
      addEventCallback: vi.fn().mockReturnValue('callback-id'),
      removeEventCallback: vi.fn(),
    },
    accounts: [],
    inProgress: 'none',
  })),
  useIsAuthenticated: vi.fn().mockReturnValue(false),
  MsalProvider: ({ children }: { children: React.ReactNode }) => children,
}));

// Mock MSAL Browser (PublicClientApplication) to avoid WebCrypto requirement
vi.mock('@azure/msal-browser', () => {
  class MockPCA {
    initialize = vi.fn().mockResolvedValue(undefined);
    handleRedirectPromise = vi.fn().mockResolvedValue(null);
    loginRedirect = vi.fn().mockResolvedValue(null);
    logoutRedirect = vi.fn().mockResolvedValue(null);
    acquireTokenSilent = vi.fn().mockResolvedValue({ accessToken: 'test-token' });
    acquireTokenRedirect = vi.fn().mockResolvedValue(null);
    getAllAccounts = vi.fn().mockReturnValue([]);
    setActiveAccount = vi.fn();
  }
  const LogLevel = { Error: 0, Warning: 1, Info: 2, Verbose: 3 } as const;
  return { PublicClientApplication: MockPCA, LogLevel };
});

// Mock Electron IPC
const mockElectron = {
  on: vi.fn(),
  send: vi.fn(),
  removeAllListeners: vi.fn(),
  getZoom: vi.fn(() => 1),
  setZoom: vi.fn(),
};

// Mock window.electron
Object.defineProperty(window, 'electron', {
  value: mockElectron,
  writable: true,
});

// Mock console.error to avoid noise in tests
const originalConsoleError = console.error;
beforeAll(() => {
  console.error = (...args: any[]) => {
    if (
      typeof args[0] === 'string' &&
      (args[0].includes('React Router Future Flag Warning') ||
       args[0].includes('Warning:'))
    ) {
      return;
    }
    originalConsoleError.apply(console, args);
  };

  // Mock File constructor to properly store content
  global.File = class MockFile {
    name: string;
    type: string;
    size: number;
    lastModified: number;
    content: string;
    
    constructor(content: any[], filename: string, options?: FilePropertyBag) {
      this.name = filename;
      this.type = options?.type || '';
      this.content = Array.isArray(content) ? content.join('') : String(content);
      this.size = this.content.length;
      this.lastModified = Date.now();
    }
    
    stream() {
      const content = this.content;
      return new ReadableStream({
        start(controller) {
          controller.enqueue(new TextEncoder().encode(content));
          controller.close();
        }
      });
    }
    
    text() {
      return Promise.resolve(this.content);
    }
    
    arrayBuffer() {
      return Promise.resolve(new TextEncoder().encode(this.content).buffer);
    }
  } as any;

  // Mock FileReader for testing File objects
  global.FileReader = class MockFileReader {
    result: string | ArrayBuffer | null = null;
    error: any = null;
    readyState = 0;
    
    onload: ((ev: any) => void) | null = null;
    onerror: ((ev: any) => void) | null = null;
    onloadend: ((ev: any) => void) | null = null;
    onloadstart: ((ev: any) => void) | null = null;
    onprogress: ((ev: any) => void) | null = null;
    onabort: ((ev: any) => void) | null = null;
    
    readAsText(file: File) {
      setTimeout(() => {
        // Extract the content from the MockFile object
        if (file && typeof file === 'object' && (file as any).content) {
          this.result = (file as any).content;
        } else {
          this.result = 'mock-file-content';
        }
        this.readyState = 2;
        if (this.onload) {
          this.onload({ target: this });
        }
      }, 0);
    }
    
    abort() {}
    addEventListener() {}
    removeEventListener() {}
    dispatchEvent() { return true; }
    
    static readonly EMPTY = 0;
    static readonly LOADING = 1;
    static readonly DONE = 2;
    
    readonly EMPTY = 0;
    readonly LOADING = 1;
    readonly DONE = 2;
  } as any;

  // Mock DOM methods for Lexical editor in test environment
  global.Range.prototype.getBoundingClientRect = vi.fn(() => ({
    bottom: 0,
    height: 0,
    left: 0,
    right: 0,
    top: 0,
    width: 0,
    x: 0,
    y: 0,
    toJSON: vi.fn()
  }));

  global.Range.prototype.getClientRects = vi.fn(() => ({
    item: vi.fn(),
    length: 0,
    [Symbol.iterator]: vi.fn()
  }));

  Element.prototype.getBoundingClientRect = vi.fn(() => ({
    bottom: 0,
    height: 0,
    left: 0,
    right: 0,
    top: 0,
    width: 0,
    x: 0,
    y: 0,
    toJSON: vi.fn()
  }));

  Element.prototype.getClientRects = vi.fn(() => ({
    item: vi.fn(),
    length: 0,
    [Symbol.iterator]: vi.fn()
  }));

  // Mock scrollIntoView
  Element.prototype.scrollIntoView = vi.fn();

  // Mock Intersection Observer
  global.IntersectionObserver = vi.fn().mockImplementation(() => ({
    observe: vi.fn(),
    unobserve: vi.fn(),
    disconnect: vi.fn()
  }));

  // Mock ResizeObserver
  // @ts-ignore
  global.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
});

afterAll(() => {
  console.error = originalConsoleError;
});

// Mock ConversationContext to provide minimal implementation for components that need it
vi.mock('../contexts/ConversationContext', () => {
  return {
    ConversationProvider: ({ children }: { children: any }) => children,
    useConversation: () => ({
      sendMessage: vi.fn(),
      editAssistantMessage: vi.fn(),
      undoLastTurn: vi.fn(),
      setSelectedAssistant: vi.fn(),
      setDraftUserContent: vi.fn(),
      startEditingAssistant: vi.fn(),
      cancelEditingAssistant: vi.fn(),
      refresh: vi.fn(),
      assistants: [],
      conversationStarters: [],
      isEditLoading: false,
      isInitialized: true,
      isCancelling: false,
      isStreaming: false,
      messages: [],
      draftUserContent: '',
      pendingAttachments: [],
      addPendingAttachment: vi.fn(),
      removePendingAttachment: vi.fn(),
      handleStreamingEvent: vi.fn(),
      cancelStream: vi.fn(),
    }),
  };
});
