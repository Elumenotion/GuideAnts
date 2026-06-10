import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';

import { PublishToProjectDialog } from '../PublishToProjectDialog';
import type { NotebookFileDto } from '../../../../types/notebook';
import type { ProjectFolderDto } from '../../../../types/project';

const makeFile = (overrides: Partial<NotebookFileDto> = {}): NotebookFileDto => ({
  id: 'file-1',
  fileName: 'notes.md',
  relativePath: 'notes.md',
  fileSize: 100,
  lastModifiedUtc: '2024-01-01T00:00:00Z',
  fileHash: 'hash-1',
  isIndexed: false,
  ...overrides,
});

const folders: ProjectFolderDto[] = [
  { id: 'folder-root-child', name: 'Docs', parentFolderId: undefined },
  { id: 'folder-nested', name: 'Specs', parentFolderId: 'folder-root-child' },
];

const defaultProps = {
  isOpen: true,
  onClose: vi.fn(),
  notebookFiles: [makeFile()],
  projectFolders: folders,
  projectTitle: 'Acme Project',
  onPublish: vi.fn().mockResolvedValue('content-file-1'),
  onComplete: vi.fn(),
  originFileInfoMap: new Map(),
};

describe('PublishToProjectDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders nothing when closed', () => {
    const { container } = render(<PublishToProjectDialog {...defaultProps} isOpen={false} />);
    expect(container.firstChild).toBeNull();
  });

  it('renders single-file publish UI', () => {
    render(<PublishToProjectDialog {...defaultProps} />);
    expect(screen.getByText('Publish to Project')).toBeInTheDocument();
    expect(screen.getByText('notes.md')).toBeInTheDocument();
    expect(screen.getByLabelText(/Destination Folder/)).toBeInTheDocument();
  });

  it('publishes a new file to selected folder and completes', async () => {
    const user = userEvent.setup();
    const onPublish = vi.fn().mockResolvedValue('published-id');
    const onClose = vi.fn();
    const onComplete = vi.fn();

    render(
      <PublishToProjectDialog
        {...defaultProps}
        onPublish={onPublish}
        onClose={onClose}
        onComplete={onComplete}
      />
    );

    await user.selectOptions(screen.getByLabelText(/Destination Folder/), 'folder-root-child');
    await user.click(screen.getByRole('button', { name: /Publish$/ }));

    await waitFor(() => {
      expect(onPublish).toHaveBeenCalledWith({
        notebookFileId: 'file-1',
        destinationFolderId: 'folder-root-child',
      });
      expect(onClose).toHaveBeenCalled();
      expect(onComplete).toHaveBeenCalledWith(['published-id']);
    });
  });

  it('publishes lineage file without destination folder', async () => {
    const user = userEvent.setup();
    const onPublish = vi.fn().mockResolvedValue('version-id');
    const lineageFile = makeFile({
      id: 'lineage-1',
      fileName: 'from-project.md',
      originContentFileVersionId: 'origin-v1',
    });
    const originFileInfoMap = new Map([
      [
        'lineage-1',
        {
          fileName: 'from-project.md',
          folderPath: 'Acme Project/Docs/from-project.md',
          contentFileId: 'cf-1',
          versionNumber: 2,
        },
      ],
    ]);

    render(
      <PublishToProjectDialog
        {...defaultProps}
        notebookFiles={[lineageFile]}
        onPublish={onPublish}
        originFileInfoMap={originFileInfoMap}
      />
    );

    expect(screen.getByText(/Creating new version/)).toBeInTheDocument();
    expect(screen.queryByLabelText(/Destination Folder/)).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Publish$/ }));

    await waitFor(() => {
      expect(onPublish).toHaveBeenCalledWith({
        notebookFileId: 'lineage-1',
        destinationFolderId: undefined,
      });
    });
  });

  it('publishes batch with mixed lineage and new files', async () => {
    const user = userEvent.setup();
    const onPublish = vi
      .fn()
      .mockResolvedValueOnce('published-a')
      .mockResolvedValueOnce('published-b');
    const files = [
      makeFile({ id: 'new-1', fileName: 'brand-new.md' }),
      makeFile({
        id: 'lineage-1',
        fileName: 'existing.md',
        originContentFileVersionId: 'origin-v1',
      }),
    ];

    render(
      <PublishToProjectDialog
        {...defaultProps}
        notebookFiles={files}
        onPublish={onPublish}
      />
    );

    expect(screen.getByRole('heading', { name: 'Publish to Project' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Publish 2 Files/ })).toBeInTheDocument();
    expect(
      screen.getByText(/originated from the project and will be published as new versions/i)
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Publish 2 Files/ }));

    await waitFor(() => {
      expect(onPublish).toHaveBeenCalledTimes(2);
      expect(onPublish).toHaveBeenNthCalledWith(1, {
        notebookFileId: 'new-1',
        destinationFolderId: undefined,
      });
      expect(onPublish).toHaveBeenNthCalledWith(2, {
        notebookFileId: 'lineage-1',
        destinationFolderId: undefined,
      });
    });
  });

  it('shows per-file error and keeps dialog open when publish fails', async () => {
    const user = userEvent.setup();
    const onPublish = vi.fn().mockRejectedValue(new Error('Publish failed'));
    const onClose = vi.fn();

    render(
      <PublishToProjectDialog {...defaultProps} onPublish={onPublish} onClose={onClose} />
    );

    await user.click(screen.getByRole('button', { name: /Publish$/ }));

    await waitFor(() => {
      expect(screen.getByText('Publish failed')).toBeInTheDocument();
    });
    expect(onClose).not.toHaveBeenCalled();
  });

  it('closes on Escape when not publishing', () => {
    const onClose = vi.fn();
    render(<PublishToProjectDialog {...defaultProps} onClose={onClose} />);

    fireEvent.keyDown(window, { key: 'Escape' });
    expect(onClose).toHaveBeenCalled();
  });

  it('publishes on Enter key', async () => {
    const onPublish = vi.fn().mockResolvedValue('published-id');
    render(<PublishToProjectDialog {...defaultProps} onPublish={onPublish} />);

    fireEvent.keyDown(window, { key: 'Enter' });

    await waitFor(() => {
      expect(onPublish).toHaveBeenCalled();
    });
  });

  it('builds nested folder paths in the destination select', () => {
    render(<PublishToProjectDialog {...defaultProps} />);
    expect(screen.getByText('Acme Project/Docs')).toBeInTheDocument();
    expect(screen.getByText('Acme Project/Docs/Specs')).toBeInTheDocument();
  });
});
