import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '../../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import { CreateNotebookDialog } from '../CreateNotebookDialog';
import { NotebookTemplateSummaryDto } from '../../../../types/project';
import { api } from '../../../../services/api';

const mockTemplates: NotebookTemplateSummaryDto[] = [
  { id: 'tmpl-1', templateName: 'Template One', description: 'Desc 1', avatarUrl: '/api/notebook-templates/avatar/Template%20One' },
  { id: 'tmpl-2', templateName: 'Template Two', description: 'Research guide', avatarUrl: '/api/notebook-templates/avatar/Template%20Two' },
  { id: 'tmpl-hidden', templateName: 'Code Notebook', description: 'Hidden', avatarUrl: undefined },
];

vi.mock('../../../../services/api', () => ({
  api: {
    projects: {
      notebookTemplates: {
        getAll: vi.fn(),
      },
    },
  },
}));

const renderDialog = (props?: Partial<React.ComponentProps<typeof CreateNotebookDialog>>) => {
  const defaultProps = {
    projectId: 'project-1',
    isOpen: true,
    onClose: vi.fn(),
    onCreate: vi.fn().mockResolvedValue(undefined),
  } as React.ComponentProps<typeof CreateNotebookDialog>;

  return render(<CreateNotebookDialog {...defaultProps} {...props} />);
};

describe('CreateNotebookDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    (api.projects.notebookTemplates.getAll as ReturnType<typeof vi.fn>).mockResolvedValue(mockTemplates);
  });

  it('renders nothing when closed', () => {
    const { container } = render(
      <CreateNotebookDialog projectId="project-1" isOpen={false} onClose={vi.fn()} onCreate={vi.fn()} />
    );
    expect(container.firstChild).toBeNull();
  });

  it('closes dialog without calling onCreate when Cancel clicked', async () => {
    const createSpy = vi.fn();
    const closeSpy = vi.fn();
    renderDialog({ onCreate: createSpy, onClose: closeSpy });

    await userEvent.click(screen.getByRole('button', { name: /cancel/i }));

    expect(createSpy).not.toHaveBeenCalled();
    expect(closeSpy).toHaveBeenCalled();
  });

  it('loads templates and selects the first visible template by default', async () => {
    renderDialog();

    await waitFor(() => {
      expect(api.projects.notebookTemplates.getAll).toHaveBeenCalledWith('project-1');
    });

    expect(await screen.findByDisplayValue('Template One')).toBeInTheDocument();
    expect(screen.queryByText('Code Notebook')).not.toBeInTheDocument();
  });

  it('creates notebook with title, template, and description', async () => {
    const onCreate = vi.fn().mockResolvedValue(undefined);
    const onClose = vi.fn();
    renderDialog({ onCreate, onClose });

    await screen.findByDisplayValue('Template One');
    await userEvent.clear(screen.getByLabelText(/title/i));
    await userEvent.type(screen.getByLabelText(/title/i), 'My Notebook');
    await userEvent.type(screen.getByLabelText(/description/i), 'For experiments');

    await userEvent.click(screen.getByRole('button', { name: /^create$/i }));

    await waitFor(() => {
      expect(onCreate).toHaveBeenCalledWith('My Notebook', 'tmpl-1', 'For experiments');
      expect(onClose).toHaveBeenCalled();
    });
  });

  it('generates smart title when creating from a single file', async () => {
    renderDialog({
      initialFiles: [{ id: 'f1', fileName: 'quarterly_report.pdf' }],
    });

    expect(await screen.findByDisplayValue('Analysis - Quarterly Report')).toBeInTheDocument();
    expect(screen.getByText(/quarterly_report\.pdf will be copied/i)).toBeInTheDocument();
  });

  it('filters guides by search query', async () => {
    renderDialog();

    await screen.findByText('Template One');
    await userEvent.type(screen.getByLabelText(/search guides/i), 'research');

    expect(screen.getByText('Template Two')).toBeInTheDocument();
    expect(screen.queryByText('Template One')).not.toBeInTheDocument();
    expect(screen.queryByText(/no guides match your search/i)).not.toBeInTheDocument();
  });

  it('shows empty search message when no guides match', async () => {
    renderDialog();

    await screen.findByText('Template One');
    await userEvent.type(screen.getByLabelText(/search guides/i), 'zzzz-not-found');

    expect(screen.getByText(/no guides match your search/i)).toBeInTheDocument();
  });

  it('closes on Escape key', async () => {
    const onClose = vi.fn();
    renderDialog({ onClose });

    await screen.findByText('New Notebook');
    await userEvent.keyboard('{Escape}');

    expect(onClose).toHaveBeenCalled();
  });
});
