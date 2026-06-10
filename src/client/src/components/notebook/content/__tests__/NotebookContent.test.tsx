import React from 'react';

import { describe, it, expect, vi, beforeEach } from 'vitest';

import { render, screen, fireEvent, waitFor } from '@testing-library/react';

import '@testing-library/jest-dom';



import { NotebookContent } from '../NotebookContent';

import type { ProjectNotebook } from '../../../../types/project';

import type { NotebookCell } from '../../../../types/notebook';



const baseNotebook: ProjectNotebook = {

  id: 'nb-1',

  title: 'Notebook Title',

} as ProjectNotebook;



function createCells(): NotebookCell[] {

  return [

    { id: 'cell-1', type: 'markdown', content: '# Heading', order: 1, created: '', modified: '' },

    { id: 'cell-2', type: 'code', content: 'print("hi")', language: 'python', order: 2, created: '', modified: '' },

    { id: 'cell-3', type: 'text', content: 'Plain text content', order: 3, created: '', modified: '' },

  ];

}



vi.mock('../../../../contexts/NotebookContext', () => ({

  useNotebook: () => mockContext,

}));



let mockContext: ReturnType<typeof getMockContext>;



function getMockContext(overrides: Partial<typeof mockContext> = {}) {

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

  beforeEach(() => {

    mockContext = getMockContext();

  });



  it('shows loading indicator when notebook is null', () => {

    mockContext = getMockContext({ notebook: null });

    const { container } = render(<NotebookContent notebook={baseNotebook} />);

    expect(container).toHaveTextContent('Loading notebook content');

  });



  it('renders cell list and triggers run action', () => {

    mockContext = getMockContext();

    render(<NotebookContent notebook={baseNotebook} canEdit />);



    expect(screen.getByText('Heading')).toBeInTheDocument();



    const runBtn = screen.getByRole('button', { name: 'Run' });

    fireEvent.click(runBtn);

    expect(mockContext.executeCell).toHaveBeenCalledWith('cell-2');

  });



  it('renders notebook description when provided', () => {

    const notebookWithDescription = {

      ...baseNotebook,

      description: 'This is a sample notebook description.\nIt can have multiple lines.',

    };

    mockContext = getMockContext();



    render(<NotebookContent notebook={notebookWithDescription} />);



    const descriptionContainer = screen.getByText((content, element) => {

      return (

        (element?.classList.contains('whitespace-pre-wrap') &&

          element?.textContent?.includes('This is a sample notebook description.') &&

          element?.textContent?.includes('It can have multiple lines.')) ||

        false

      );

    });



    expect(descriptionContainer).toBeInTheDocument();

  });



  it('displays notebook title and cells', () => {

    mockContext = getMockContext();

    render(<NotebookContent notebook={baseNotebook} />);

    expect(screen.getByText('Notebook Title')).toBeInTheDocument();

    expect(screen.getByText('Heading')).toBeInTheDocument();

  });



  it('does not render description section when no description is provided', () => {

    mockContext = getMockContext();

    render(<NotebookContent notebook={baseNotebook} />);

    expect(screen.queryByText('description')).not.toBeInTheDocument();

  });



  it('shows empty-state message when notebook has no cells', () => {

    mockContext = getMockContext({

      notebook: {

        ...getMockContext().notebook!,

        cells: [],

      },

    });



    render(<NotebookContent notebook={baseNotebook} />);



    expect(screen.getByText('Welcome to your notebook')).toBeInTheDocument();

    expect(screen.getByText(/Select a conversation or file/i)).toBeInTheDocument();

  });



  it('renders text cell content and unknown cell type fallback', () => {

    mockContext = getMockContext({

      notebook: {

        ...getMockContext().notebook!,

        cells: [

          ...createCells(),

          {

            id: 'cell-unknown',

            type: 'widget' as NotebookCell['type'],

            content: '',

            order: 4,

            created: '',

            modified: '',

          },

        ],

      },

    });



    render(<NotebookContent notebook={baseNotebook} />);



    expect(screen.getByText('Plain text content')).toBeInTheDocument();

    expect(screen.getByText('Unknown cell type')).toBeInTheDocument();

  });



  it('renders code cell output when present', () => {

    mockContext = getMockContext({

      notebook: {

        ...getMockContext().notebook!,

        cells: [

          {

            id: 'cell-2',

            type: 'code',

            content: 'print("hi")',

            language: 'python',

            order: 1,

            created: '',

            modified: '',

            output: 'hi',

          },

        ],

      },

    });



    render(<NotebookContent notebook={baseNotebook} />);

    expect(screen.getByText('Output:')).toBeInTheDocument();

    expect(screen.getByText('hi')).toBeInTheDocument();

  });



  it('shows executing state on the active code cell', () => {

    mockContext = getMockContext({

      isExecuting: true,

      executingCellId: 'cell-2',

    });



    render(<NotebookContent notebook={baseNotebook} canEdit />);

    expect(screen.getByRole('button', { name: 'Executing...' })).toBeDisabled();

  });



  it('hides edit and run controls when canEdit is false', () => {

    mockContext = getMockContext();

    render(<NotebookContent notebook={baseNotebook} canEdit={false} />);



    expect(screen.queryByText('Edit')).not.toBeInTheDocument();

    expect(screen.queryByRole('button', { name: 'Run' })).not.toBeInTheDocument();

  });



  it('enters edit mode, saves changes, and cancels edits', async () => {

    mockContext = getMockContext();

    render(<NotebookContent notebook={baseNotebook} canEdit />);



    fireEvent.click(screen.getAllByText('Edit')[0]);

    const textarea = screen.getByRole('textbox');

    fireEvent.change(textarea, { target: { value: '# Updated heading' } });

    fireEvent.click(screen.getByRole('button', { name: 'Save' }));



    await waitFor(() => {

      expect(mockContext.updateCell).toHaveBeenCalledWith('cell-1', { content: '# Updated heading' });

    });



    fireEvent.click(screen.getAllByText('Edit')[0]);

    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'temporary' } });

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.queryByRole('textbox')).not.toBeInTheDocument();

  });



  it('selects a cell when clicking the cell container', () => {

    mockContext = getMockContext();

    render(<NotebookContent notebook={baseNotebook} />);



    fireEvent.click(screen.getByText('markdown Cell').closest('div.border')!);

    expect(mockContext.setSelectedCell).toHaveBeenCalled();

  });



  it('highlights selected cell', () => {

    mockContext = getMockContext({

      selectedCell: createCells()[0],

    });



    render(<NotebookContent notebook={baseNotebook} />);

    const selected = screen.getByText('markdown Cell').closest('div.border');

    expect(selected).toHaveClass('border-blue-500');

  });

  it('shows loading when passed notebook is null', () => {
    mockContext = getMockContext();
    const { container } = render(<NotebookContent notebook={null as unknown as ProjectNotebook} />);
    expect(container).toHaveTextContent('Loading notebook content');
  });

  it('opens external markdown links through electron when available', async () => {
    const openExternal = vi.fn();
    const win = window as unknown as { electron?: { openExternal: typeof openExternal } };
    const originalElectron = win.electron;
    win.electron = { openExternal };

    mockContext = getMockContext({
      notebook: {
        ...getMockContext().notebook!,
        cells: [{
          id: 'md-link',
          type: 'markdown',
          content: 'See [docs](https://example.com/docs)',
          order: 1,
          created: '',
          modified: '',
        }],
      },
    });

    render(<NotebookContent notebook={baseNotebook} />);
    const link = await screen.findByRole('link', { name: 'docs' });
    fireEvent.click(link);
    expect(openExternal).toHaveBeenCalledWith('https://example.com/docs');

    win.electron = originalElectron;
  });

});


