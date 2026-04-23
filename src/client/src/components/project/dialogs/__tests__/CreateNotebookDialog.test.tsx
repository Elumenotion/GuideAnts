import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '../../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import { CreateNotebookDialog } from '../CreateNotebookDialog';
import { NotebookTemplateSummaryDto } from '../../../../types/project';

// Mock api before components are imported (hoisted)
vi.mock('../../../../services/api', () => {
  const mockTemplates: NotebookTemplateSummaryDto[] = [
    { id: 'tmpl-1', templateName: 'Template One', description: 'Desc 1', avatarUrl: '/api/notebook-templates/avatar/Template%20One' },
    { id: 'tmpl-2', templateName: 'Template Two', description: 'Desc 2', avatarUrl: '/api/notebook-templates/avatar/Template%20Two' },
  ];

  return {
    api: {
      projects: {
        notebookTemplates: {
          getAll: vi.fn().mockResolvedValue(mockTemplates),
        },
      },
    },
  };
});

const renderDialog = (props?: Partial<React.ComponentProps<typeof CreateNotebookDialog>>) => {
  const defaultProps = {
    isOpen: true,
    onClose: vi.fn(),
    onCreate: vi.fn().mockResolvedValue(undefined),
  } as React.ComponentProps<typeof CreateNotebookDialog>;

  return render(<CreateNotebookDialog {...defaultProps} {...props} />);
};

describe('CreateNotebookDialog', () => {
  beforeEach(() => {
    vi.useRealTimers();
  });

  it('renders nothing when closed', () => {
    const { container } = render(
      <CreateNotebookDialog isOpen={false} onClose={vi.fn()} onCreate={vi.fn()} />
    );
    expect(container.firstChild).toBeNull();
  });

  it('fetches templates and renders template list', async () => {
    renderDialog();

    // Header present (label text changed to "New Notebook")
    expect(screen.getByRole('heading', { name: /new notebook/i })).toBeInTheDocument();

    // Wait for templates
    const tplBtn = await screen.findByRole('button', { name: /template one/i });
    expect(tplBtn).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /template two/i })).toBeInTheDocument();

    // Avatar img should have correct src (no double /api)
    const img = screen.getByRole('img', { name: /template one/i });
    expect(img).toHaveAttribute('src', expect.stringContaining('/api/notebook-templates/avatar/Template%20One'));
  });

  it('enables Create button when title entered and calls onCreate with selected template', async () => {
    const createSpy = vi.fn().mockResolvedValue(undefined);
    const closeSpy = vi.fn();

    renderDialog({ onCreate: createSpy, onClose: closeSpy });

    // Ensure templates loaded
    await screen.findByRole('button', { name: /template two/i });

    // Title input label is "Title"
    const titleInput = screen.getByLabelText(/title/i);
    await userEvent.type(titleInput, 'My Notebook');

    // Select second template by clicking button
    const tplTwoBtn = await screen.findByRole('button', { name: /template two/i });
    await userEvent.click(tplTwoBtn);

    const createBtn = screen.getByRole('button', { name: /^create$/i });
    expect(createBtn).not.toBeDisabled();

    await userEvent.click(createBtn);

    await waitFor(() => {
      expect(createSpy).toHaveBeenCalledWith('My Notebook', 'tmpl-2', undefined);
      expect(closeSpy).toHaveBeenCalled();
    });
  });

  it('closes dialog without calling onCreate when Cancel clicked', async () => {
    const createSpy = vi.fn();
    const closeSpy = vi.fn();
    renderDialog({ onCreate: createSpy, onClose: closeSpy });

    const cancelBtn = screen.getByRole('button', { name: /cancel/i });
    await userEvent.click(cancelBtn);

    expect(createSpy).not.toHaveBeenCalled();
    expect(closeSpy).toHaveBeenCalled();
  });

  it('sets title on open from the initially selected guide and updates on guide change if unchanged', async () => {
    renderDialog();

    // Wait for templates to load
    await screen.findByRole('button', { name: /template one/i });

    // On open, title should be auto-set to first visible guide (alphabetical => Template One)
    const titleInput = screen.getByLabelText(/title/i) as HTMLInputElement;
    expect(titleInput.value).toBe('Template One');

    // Change guide selection; since title is still unchanged, it should update to Template Two
    const tplTwoBtn = screen.getByRole('button', { name: /template two/i });
    await userEvent.click(tplTwoBtn);
    expect(titleInput.value).toBe('Template Two');
  });

  it('does not overwrite a manually edited title when guide selection changes', async () => {
    renderDialog();

    await screen.findByRole('button', { name: /template one/i });

    const titleInput = screen.getByLabelText(/title/i) as HTMLInputElement;

    // User types a custom title
    await userEvent.clear(titleInput);
    await userEvent.type(titleInput, 'Custom Title');
    expect(titleInput.value).toBe('Custom Title');

    // Change guide selection; title should remain the user-entered value
    const tplTwoBtn = screen.getByRole('button', { name: /template two/i });
    await userEvent.click(tplTwoBtn);

    expect(titleInput.value).toBe('Custom Title');
  });
}); 