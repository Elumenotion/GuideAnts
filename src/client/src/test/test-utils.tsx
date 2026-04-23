import React from 'react';
import { render as rtlRender } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { DndProvider } from 'react-dnd';
import { HTML5Backend } from 'react-dnd-html5-backend';
import { ToastProvider } from '../components/common/Toast';
import { ProjectProvider } from '../contexts/ProjectContext';
import { NotebookProvider } from '../contexts/NotebookContext';
import { ConversationProvider } from '../contexts/ConversationContext';
import { vi } from 'vitest';

// Mock all MSAL-related functionality
vi.mock('@azure/msal-react', () => ({
  MsalProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
  useIsAuthenticated: vi.fn().mockReturnValue(false),
  useMsal: vi.fn().mockReturnValue({
    instance: {
      handleRedirectPromise: vi.fn().mockResolvedValue(null),
      loginRedirect: vi.fn().mockResolvedValue(null),
      logoutRedirect: vi.fn().mockResolvedValue(null),
      getAllAccounts: vi.fn().mockReturnValue([]),
    },
    accounts: [],
  }),
}));

interface RenderOptions {
  initialRoute?: string;
  [key: string]: any;
}

function render(
  ui: React.ReactElement,
  { initialRoute = '/', ...renderOptions }: RenderOptions = {}
) {
  const Wrapper = ({ children }: { children: React.ReactNode }) => (
    <ToastProvider>
      <DndProvider backend={HTML5Backend}>
        <MemoryRouter initialEntries={[initialRoute]} initialIndex={0}>
          <ProjectProvider projectId={undefined}>
            <NotebookProvider projectId={undefined} notebookId={undefined}>
              <ConversationProvider projectId="test-project" notebookId="test-notebook" conversationId="test-convo">
                {children}
              </ConversationProvider>
            </NotebookProvider>
          </ProjectProvider>
        </MemoryRouter>
      </DndProvider>
    </ToastProvider>
  );

  return rtlRender(ui, { wrapper: Wrapper, ...renderOptions });
}

export * from '@testing-library/react';
export { render }; 