import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '../../../../test/test-utils';
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

  it('closes dialog without calling onCreate when Cancel clicked', async () => {
    const createSpy = vi.fn();
    const closeSpy = vi.fn();
    renderDialog({ onCreate: createSpy, onClose: closeSpy });

    const cancelBtn = screen.getByRole('button', { name: /cancel/i });
    await userEvent.click(cancelBtn);

    expect(createSpy).not.toHaveBeenCalled();
    expect(closeSpy).toHaveBeenCalled();
  });
}); 