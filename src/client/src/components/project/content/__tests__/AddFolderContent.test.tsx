// @ts-nocheck
import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, act } from '../../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import { AddFolderContent } from '../AddFolderContent';
import { ProjectFolderDto } from '../../../../types/project';

const rootFolder: ProjectFolderDto = {
  id: 'root',
  name: 'Test Project',
  relativePath: '',
  parentFolderId: undefined,
} as ProjectFolderDto;

const childFolder: ProjectFolderDto = {
  id: 'f1',
  name: 'Docs',
  relativePath: 'Docs',
  parentFolderId: undefined,
} as ProjectFolderDto;

const grandChildFolder: ProjectFolderDto = {
  id: 'f2',
  name: 'Sub',
  relativePath: 'Docs/Sub',
  parentFolderId: 'f1',
} as ProjectFolderDto;

const allFolders = [rootFolder, childFolder, grandChildFolder];

const setup = (extraProps: Partial<React.ComponentProps<typeof AddFolderContent>> = {}) => {
  const defaultProps: React.ComponentProps<typeof AddFolderContent> = {
    onAdd: vi.fn(async (_data: any): Promise<void> => {}) as (data: any) => Promise<void>,
    onCancel: vi.fn(),
    folders: allFolders,
  } as any;

  return render(<AddFolderContent {...defaultProps} {...extraProps} />);
};

describe('AddFolderContent', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders form fields and folder options', () => {
    setup();

    // root option plus two folders (root + sub)
    expect(screen.getByLabelText(/folder name/i)).toBeInTheDocument();
    const options = screen.getAllByRole('option');
    expect(options.map(o => o.textContent)).toEqual(['Root', 'Docs', 'Docs/Sub', 'Test Project']);
  });

  it('prevents duplicate folder creation in same parent', async () => {
    setup();

    // Type duplicate name "Docs" which already exists at root
    await userEvent.type(screen.getByLabelText(/folder name/i), 'Docs');
    await userEvent.click(screen.getByRole('button', { name: /create folder/i }));

    expect(await screen.findByText(/already exists/i)).toBeInTheDocument();
  });

  it('calls onAdd with trimmed name and selected parent folder', async () => {
    const onAdd = vi.fn(async (_data: any): Promise<void> => {}) as (data: any) => Promise<void>;
    const onCancel = vi.fn();

    setup({ onAdd, onCancel });

    // Select parent folder "Docs" then type name
    await userEvent.selectOptions(screen.getByLabelText(/parent folder/i), 'f1');
    await userEvent.type(screen.getByLabelText(/folder name/i), '  New   ');

    await userEvent.click(screen.getByRole('button', { name: /create folder/i }));

    await waitFor(() => {
      expect(onAdd).toHaveBeenCalledWith({ name: 'New', parentFolderId: 'f1' });
      expect(onCancel).toHaveBeenCalled();
    });
  });

  it('disables inputs while creating', async () => {
    const onAdd = vi.fn(() => new Promise(res => setTimeout(res, 100))); // delay to observe state
    setup({ onAdd });

    await userEvent.type(screen.getByLabelText(/folder name/i), 'Async');

    // trigger submit
    await act(async () => {
      await userEvent.click(screen.getByRole('button', { name: /create folder/i }));
    });

    // Wait for the state to update and button to show Creating... text and be disabled
    await waitFor(
      () => {
        const creatingButton = screen.getByRole('button', { name: /creating/i });
        expect(creatingButton).toBeDisabled();
      },
      { timeout: 1000 }
    );
  });

  it('invokes onCancel when cancel button is clicked', async () => {
    const onCancel = vi.fn();
    setup({ onCancel });

    await userEvent.click(screen.getByRole('button', { name: /^cancel$/i }));
    expect(onCancel).toHaveBeenCalled();
  });
}); 