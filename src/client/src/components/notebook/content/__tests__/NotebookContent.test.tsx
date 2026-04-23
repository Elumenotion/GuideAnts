import React from 'react';
import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';

import { NotebookContent } from '../NotebookContent';
import type { ProjectNotebook } from '../../../../types/project';
import type { NotebookCell } from '../../../../types/notebook';

// Helper notebook value
const baseNotebook: ProjectNotebook = {
  id: 'nb-1',
  title: 'Notebook Title',
} as ProjectNotebook;

function createCells(): NotebookCell[] {
  return [
    { id: 'cell-1', type: 'markdown', content: '# Heading', order: 1, created: '', modified: '' },
    { id: 'cell-2', type: 'code', content: 'print("hi")', language: 'python', order: 2, created: '', modified: '' },
  ];
}

// Mock useNotebook hook similarly to sidebar tests
vi.mock('../../../../contexts/NotebookContext', () => {
  return {
    useNotebook: () => mockContext,
  };
});

let mockContext: any;

function getMockContext(overrides: Partial<any> = {}) {
  return {
    notebook: {
      id: 'nb-1',
      title: 'Notebook Title',
      projectId: 'proj',
      cells: createCells(),
      created: '2023-01-01T00:00:00Z',
      modified: '2023-01-01T00:00:00Z',
      createdBy: 'user-1',
      modifiedBy: 'user-1',
    },
    selectedCell: null,
    setSelectedCell: vi.fn(),
    updateCell: vi.fn().mockResolvedValue(undefined),
    executeCell: vi.fn().mockResolvedValue(undefined),
    isExecuting: false,
    executingCellId: null,
    ...overrides,
  };
}

describe('NotebookContent', () => {
  it('shows loading indicator when notebook is null', () => {
    mockContext = getMockContext({ notebook: null });
    const { container } = render(<NotebookContent notebook={baseNotebook} />);
    expect(container).toHaveTextContent('Loading notebook content');
  });

  it('renders cell list & triggers actions', async () => {
    mockContext = getMockContext();
    render(<NotebookContent notebook={baseNotebook} canEdit />);

    // Markdown content rendered as heading
    expect(screen.getByText('Heading')).toBeInTheDocument();

    // Run button for code cell should be visible and clickable
    const runBtn = screen.getByRole('button', { name: 'Run' });
    fireEvent.click(runBtn);
    expect(mockContext.executeCell).toHaveBeenCalledWith('cell-2');

    // Edit button for markdown cell
    const editBtn = screen.getAllByText('Edit')[0];
    fireEvent.click(editBtn);

    // textarea appears
    expect(screen.getByRole('textbox')).toBeInTheDocument();
  });

  it('renders notebook description when provided', () => {
    const notebookWithDescription = {
      ...baseNotebook,
      description: 'This is a sample notebook description.\nIt can have multiple lines.'
    };
    mockContext = getMockContext();
    
    render(<NotebookContent notebook={notebookWithDescription} />);
    
    // Find the description container specifically
    const descriptionContainer = screen.getByText((content, element) => {
      return (
        element?.classList.contains('whitespace-pre-wrap') &&
        element?.textContent?.includes('This is a sample notebook description.') &&
        element?.textContent?.includes('It can have multiple lines.')
      ) || false;
    });
    
    expect(descriptionContainer).toBeInTheDocument();
  });

  it('displays notebook title and description section', () => {
    mockContext = getMockContext();
    
    render(<NotebookContent notebook={baseNotebook} />);
    
    // Should show notebook title
    expect(screen.getByText('Notebook Title')).toBeInTheDocument();
    // Metadata section was removed per design update, so we just verify basic rendering
    expect(screen.getByText('Heading')).toBeInTheDocument(); // From markdown cell
  });

  it('does not render description section when no description is provided', () => {
    mockContext = getMockContext();
    
    render(<NotebookContent notebook={baseNotebook} />);
    
    // Should not have a description section
    expect(screen.queryByText('description')).not.toBeInTheDocument();
  });
}); 