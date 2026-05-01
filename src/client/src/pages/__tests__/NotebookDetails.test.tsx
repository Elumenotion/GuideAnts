import React from 'react';
import { describe, it, expect, vi } from 'vitest';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';

import NotebookDetails from '../NotebookDetails';

// Mock dependent contexts & components to isolate NotebookDetails logic
vi.mock('../../contexts/ProjectContext', () => {
  return {
    useProject: () => mockProjectContext,
  };
});

vi.mock('../../contexts/NotebookContext', () => {
  return {
    NotebookProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
    useNotebook: () => mockNotebookContext,
  };
});

vi.mock('../../components/layouts/NotebookLayout', () => ({
  NotebookLayout: (props: any) => (
    <div data-testid="notebook-layout">
      {props.notebook?.title}
      <div>{props.sidebar}</div>
      <div>{props.content}</div>
    </div>
  ),
}));
vi.mock('../../components/layouts/SidebarContainer', () => ({ SidebarContainer: ({ children }: any) => <div>{children}</div> }));
vi.mock('../../components/notebook/sidebar/NotebookSidebar', () => ({ NotebookSidebar: () => <div data-testid="sidebar" /> }));
vi.mock('../../components/notebook/content/NotebookContent', () => ({ NotebookContent: () => <div data-testid="notebook-content" /> }));
vi.mock('../../components/LoadingSpinner', () => ({ default: () => <div data-testid="loading" /> }));
vi.mock('../../components/ErrorScreen', () => ({ default: ({ title, error }: any) => <div data-testid="error">{title}:{error}</div> }));

vi.mock('../../tour/useRegisterTour', () => ({
  useRegisterTour: vi.fn()
}));

// Mock the API service
vi.mock('../../services/api', () => ({
  api: {
    projects: {
      notebookTemplates: {
        getAll: vi.fn().mockResolvedValue([]),
        getById: vi.fn().mockResolvedValue({ id: 'guide-1' }),
      },
    },
  },
}));

let mockProjectContext: any;
let mockNotebookContext: any;

function renderWithRoute(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/projects/:projectId/notebooks/:notebookId" element={<NotebookDetails />} />
        <Route path="*" element={<NotebookDetails />} />
      </Routes>
    </MemoryRouter>
  );
}

describe('NotebookDetails page', () => {
  it('renders error when params missing', () => {
    mockProjectContext = {};
    mockNotebookContext = {};
    renderWithRoute('/invalid');
    expect(screen.getByTestId('error')).toHaveTextContent('Invalid URL');
  });

}); 