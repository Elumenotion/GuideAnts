import './enableConversationContextStub';
import React from 'react';
import { render as rtlRender, type RenderOptions as RTLRenderOptions } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { DndProvider } from 'react-dnd';
import { HTML5Backend } from 'react-dnd-html5-backend';
import { ToastProvider } from '../components/common/Toast';
import { ProjectProvider } from '../contexts/ProjectContext';
import { NotebookProvider } from '../contexts/NotebookContext';
import { ConversationProvider } from '../contexts/ConversationContext';
import { AuthProvider } from '../contexts/AuthContext';

interface CustomRenderOptions {
  initialRoute?: string;
  /** When true, mounts the real ConversationProvider (requires API mocks). Default: stub via enableConversationContextStub. */
  useRealConversationProvider?: boolean;
  projectId?: string;
  notebookId?: string;
  conversationId?: string;
}

type RenderOptions = CustomRenderOptions & Omit<RTLRenderOptions, 'wrapper'>;

function render(
  ui: React.ReactElement,
  {
    initialRoute = '/',
    useRealConversationProvider = false,
    projectId = 'test-project',
    notebookId = 'test-notebook',
    conversationId = 'test-convo',
    ...renderOptions
  }: RenderOptions = {}
) {
  const Wrapper = ({ children }: { children: React.ReactNode }) => {
    const conversationLayer = useRealConversationProvider ? (
      <ConversationProvider
        projectId={projectId}
        notebookId={notebookId}
        conversationId={conversationId}
      >
        {children}
      </ConversationProvider>
    ) : (
      children
    );

    return (
      <ToastProvider>
        <DndProvider backend={HTML5Backend}>
          <MemoryRouter initialEntries={[initialRoute]} initialIndex={0}>
            <AuthProvider>
              <ProjectProvider projectId={projectId}>
                <NotebookProvider projectId={projectId} notebookId={notebookId}>
                  {conversationLayer}
                </NotebookProvider>
              </ProjectProvider>
            </AuthProvider>
          </MemoryRouter>
        </DndProvider>
      </ToastProvider>
    );
  };

  return rtlRender(ui, { wrapper: Wrapper, ...renderOptions });
}

interface NotebookRouteOptions extends Omit<RenderOptions, 'initialRoute'> {
  route: string;
  projectId: string;
  notebookId: string;
  conversationId?: string;
}

/** Render at a notebook URL with project/notebook providers wired to route params. */
function renderWithNotebookRoute(
  ui: React.ReactElement,
  {
    route,
    projectId,
    notebookId,
    conversationId = 'test-convo',
    useRealConversationProvider = false,
    ...renderOptions
  }: NotebookRouteOptions
) {
  return render(
    <Routes>
      <Route path="/projects/:projectId/notebooks/:notebookId/*" element={ui} />
    </Routes>,
    {
      initialRoute: route,
      projectId,
      notebookId,
      conversationId,
      useRealConversationProvider,
      ...renderOptions,
    }
  );
}

/** Alias for render with useRealConversationProvider: true. */
function renderWithConversation(
  ui: React.ReactElement,
  options: RenderOptions = {}
) {
  return render(ui, { ...options, useRealConversationProvider: true });
}

export * from '@testing-library/react';
export { render, renderWithNotebookRoute, renderWithConversation };
export { createStubUseConversationValue, conversationContextStubModule } from './conversationContextStub'; 