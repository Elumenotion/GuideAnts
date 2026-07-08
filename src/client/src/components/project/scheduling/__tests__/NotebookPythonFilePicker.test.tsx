import { describe, expect, it, vi, beforeEach } from 'vitest';
import userEvent from '@testing-library/user-event';
import { render, screen, waitFor } from '../../../../test/test-utils';
import { NotebookPythonFilePicker } from '../NotebookPythonFilePicker';
import { api } from '../../../../services/api';

vi.mock('../../../../services/api', () => ({
  api: {
    projects: {
      notebooks: {
        getNotebookFiles: vi.fn(),
      },
    },
  },
}));

describe('NotebookPythonFilePicker', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('loads python files and reports selection', async () => {
    const user = userEvent.setup();
    const onSelect = vi.fn();

    vi.mocked(api.projects.notebooks.getNotebookFiles).mockResolvedValue([
      { id: 'file-1', relativePath: 'scripts/run.py', name: 'run.py' },
      { id: 'file-2', relativePath: 'README.md', name: 'README.md' },
    ]);

    render(
      <NotebookPythonFilePicker
        projectId="project-1"
        notebookId="notebook-1"
        onSelect={onSelect}
      />,
    );

    await waitFor(() => {
      expect(screen.getByRole('option', { name: 'scripts/run.py' })).toBeInTheDocument();
    });
    expect(screen.queryByRole('option', { name: 'README.md' })).not.toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText(/Python script/i), 'file-1');
    expect(onSelect).toHaveBeenCalledWith('file-1', 'scripts/run.py');
  });

  it('shows empty-state and error messages', async () => {
    vi.mocked(api.projects.notebooks.getNotebookFiles).mockResolvedValue([]);

    const { rerender } = render(
      <NotebookPythonFilePicker
        projectId="project-1"
        notebookId="notebook-1"
        onSelect={vi.fn()}
      />,
    );

    expect(await screen.findByText(/No \.py files found/i)).toBeInTheDocument();

    vi.mocked(api.projects.notebooks.getNotebookFiles).mockRejectedValue(new Error('Load failed'));
    rerender(
      <NotebookPythonFilePicker
        projectId="project-1"
        notebookId="notebook-2"
        onSelect={vi.fn()}
      />,
    );

    expect(await screen.findByText('Load failed')).toBeInTheDocument();
  });
});
