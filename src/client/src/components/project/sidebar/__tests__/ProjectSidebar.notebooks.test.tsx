import React from 'react';
import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '../../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import { within } from '@testing-library/react';
import { ProjectSidebar } from '../ProjectSidebar';
import { ProjectDetailsDto } from '../../../../types/project';

const sampleProject: ProjectDetailsDto = {
  id: 'p1',
  title: 'Demo',
  description: '',
  created: '',
  userRoles: [],
  notebooks: [
    { id: 'nb1', title: 'Design Notes' },
    { id: 'nb2', title: 'Research' },
  ],
  contentFiles: [],
  folders: [],
  links: [],
  semiStructuredDatas: [],
};

const setup = (overrides?: Partial<React.ComponentProps<typeof ProjectSidebar>>) => {
  const defaultProps = {
    project: sampleProject,
    expandedSections: new Set(['notebooks']),
    selectedItem: null,
    onSectionToggle: vi.fn(),
    onItemSelect: vi.fn(),
  } as React.ComponentProps<typeof ProjectSidebar>;

  return render(<ProjectSidebar {...defaultProps} {...overrides} />);
};

describe('ProjectSidebar – Notebook context menu', () => {
  it('shows Copy and Rename when callbacks provided and fires Copy', async () => {
    const copySpy = vi.fn();

    setup({ onCopyNotebook: copySpy, onRenameNotebook: vi.fn() });

    // Right-click first notebook label
    const notebookBtn = await screen.findByText('Design Notes');
    await userEvent.pointer({ keys: '[MouseRight]', target: notebookBtn });

    // Wait for Copy item to appear
    const copyItem = await screen.findByText('Copy');
    const renameItem = screen.getByText('Rename');
    expect(copyItem).toBeInTheDocument();
    expect(renameItem).toBeInTheDocument();

    await userEvent.click(copyItem);
    expect(copySpy).toHaveBeenCalledWith('nb1');
  });

  it('runs inline rename flow and calls onRenameNotebook', async () => {
    const renameSpy = vi.fn();

    setup({ onRenameNotebook: renameSpy });

    const notebookBtn = screen.getByText('Research');
    await userEvent.pointer({ keys: '[MouseRight]', target: notebookBtn });

    await userEvent.click(await screen.findByText('Rename'));

    // Input should replace the label
    const input = await screen.findByDisplayValue('Research');
    await userEvent.clear(input);
    await userEvent.type(input, 'Research Updated{enter}');

    expect(renameSpy).toHaveBeenCalledWith('nb2', 'Research Updated');
  });
}); 