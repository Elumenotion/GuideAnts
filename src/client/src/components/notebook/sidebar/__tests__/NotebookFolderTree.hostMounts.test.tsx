import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, fireEvent, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import { NotebookFolderTree } from '../NotebookFolderTree';
import { NotebookFolderTreeDto } from '../../../../types/notebook';
import { renderWithNotebookRoute } from '../../../../test/test-utils';
import type { NotebookHostMountEntry } from '../../../../types/hostFolderMount';

const mockHostMounts: NotebookHostMountEntry[] = [
  {
    mountId: 'mount-linked',
    leafName: 'Shared',
    relativePath: 'Shared',
    displayName: 'Shared data',
    displayState: 'Linked',
    mountStatus: 'Active',
    scope: 'Notebook',
    linkStatus: 'Linked',
  },
  {
    mountId: 'mount-pending',
    leafName: 'PendingHost',
    relativePath: 'PendingHost',
    displayName: 'Pending host',
    displayState: 'PendingRestart',
    mountStatus: 'PendingRestart',
    scope: 'Project',
    linkStatus: 'PendingRestart',
  },
];

vi.mock('../../../../hooks/useNotebookHostMounts', () => ({
  useNotebookHostMounts: vi.fn(),
}));

vi.mock('../../../../services/hostFolderMounts', () => ({
  hostFolderMountsApi: {
    create: vi.fn(),
    getApplyCommand: vi.fn(),
    getRemoveCommand: vi.fn(),
    reconcile: vi.fn(),
  },
}));

vi.mock('../../../../contexts/AuthContext', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../../contexts/AuthContext')>();
  return actual;
});

vi.mock('../../../../hooks/useLongPress', () => ({
  useLongPress: () => ({}),
}));

vi.mock('../../../notebook/conversations/FullScreenEditor', () => ({
  default: () => null,
}));

import { useNotebookHostMounts } from '../../../../hooks/useNotebookHostMounts';
import { hostFolderMountsApi } from '../../../../services/hostFolderMounts';

const mockTree: NotebookFolderTreeDto = {
  name: 'Notebook',
  relativePath: '',
  subFolders: [
    {
      name: 'Shared',
      relativePath: 'Shared',
      subFolders: [
        {
          name: 'inner',
          relativePath: 'Shared/inner',
          subFolders: [],
          files: [
            {
              id: 'host-file-1',
              fileName: 'host.txt',
              relativePath: 'Shared/inner/host.txt',
              fileSize: 10,
              lastModifiedUtc: '2024-01-01T00:00:00Z',
              fileHash: 'hash',
              isIndexed: false,
              index: false,
            },
          ],
        },
      ],
      files: [],
    },
    {
      name: 'Docs',
      relativePath: 'Docs',
      subFolders: [],
      files: [],
    },
  ],
  files: [],
};

const renderTree = (overrides: Partial<React.ComponentProps<typeof NotebookFolderTree>> = {}) => {
  const props = {
    tree: mockTree,
    notebookName: 'Test Notebook',
    selectedItem: null,
    onItemSelect: vi.fn(),
    canEdit: true,
    isAdmin: true,
    activeSection: 'notebookFiles' as const,
    onSectionActivate: vi.fn(),
    ...overrides,
  };

  return renderWithNotebookRoute(<NotebookFolderTree {...props} />, {
    route: '/projects/proj-1/notebooks/nb-1',
    projectId: 'proj-1',
    notebookId: 'nb-1',
  });
};

describe('NotebookFolderTree host mounts', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(useNotebookHostMounts).mockReturnValue({
      mounts: mockHostMounts,
      isLoading: false,
      error: null,
      refresh: vi.fn().mockResolvedValue(undefined),
    });
    vi.mocked(hostFolderMountsApi.getApplyCommand).mockResolvedValue({
      mountId: 'mount-linked',
      status: 'Active',
      command: 'apply-command',
    });
    vi.mocked(hostFolderMountsApi.getRemoveCommand).mockResolvedValue({
      mountId: 'mount-linked',
      status: 'PendingRemoval',
      command: 'remove-command',
    });
    vi.mocked(hostFolderMountsApi.reconcile).mockResolvedValue({
      mountId: 'mount-linked',
      status: 'Active',
      message: 'ok',
    });
  });

  it('shows admin host-mount menu actions on notebook root only', async () => {
    renderTree();

    fireEvent.contextMenu(screen.getByTitle('Test Notebook'));

    expect(screen.getByTestId('host-mount-menu-map')).toBeInTheDocument();
    expect(screen.getByTestId('host-mount-menu-check')).toBeInTheDocument();
    expect(screen.queryByTestId('host-mount-menu-remove')).not.toBeInTheDocument();
  });

  it('hides admin host-mount menu actions for non-admin users', () => {
    renderTree({ isAdmin: false });

    fireEvent.contextMenu(screen.getByTitle('Test Notebook'));

    expect(screen.queryByTestId('host-mount-menu-map')).not.toBeInTheDocument();
    expect(screen.queryByTestId('host-mount-menu-check')).not.toBeInTheDocument();
  });

  it('renders distinct display state badges for mount folders', () => {
    renderTree();

    expect(screen.getByTestId('host-mount-state-Linked')).toBeInTheDocument();
    expect(screen.getByTestId('host-mount-state-PendingRestart')).toBeInTheDocument();
  });

  it('shows remove mapped folder instead of delete on mount root', () => {
    renderTree();

    fireEvent.contextMenu(screen.getByTitle('Shared'));

    expect(screen.getByTestId('host-mount-menu-remove')).toBeInTheDocument();
    expect(screen.queryByText('Delete')).not.toBeInTheDocument();
  });

  it('does not expose host path text to non-admin users', () => {
    vi.mocked(useNotebookHostMounts).mockReturnValue({
      mounts: [],
      isLoading: false,
      error: null,
      refresh: vi.fn(),
    });

    renderTree({ isAdmin: false });

    expect(screen.queryByText('Shared data')).not.toBeInTheDocument();
    expect(screen.queryByTestId('host-mount-state-Linked')).not.toBeInTheDocument();
    expect(localStorage.getItem('hostPath')).toBeNull();
    expect(localStorage.getItem('hostCommand')).toBeNull();
  });

  it('marks files inside mapped folders as read-only in the context menu', async () => {
    const user = userEvent.setup();
    renderTree({ onDeleteFile: vi.fn() });

    const sharedRow = screen.getByTitle('Shared').closest('.group');
    const sharedToggle = sharedRow?.querySelector('button');
    expect(sharedToggle).toBeInTheDocument();
    if (sharedToggle) {
      await user.click(sharedToggle);
    }

    const innerRow = await screen.findByTitle('inner');
    const innerToggle = innerRow.closest('.group')?.querySelector('button');
    expect(innerToggle).toBeInTheDocument();
    if (innerToggle) {
      await user.click(innerToggle);
    }

    fireEvent.contextMenu(screen.getByTitle('host.txt'));
    expect(screen.getByText('Linked files are read-only here.')).toBeInTheDocument();
    expect(screen.queryByText('Delete on host')).not.toBeInTheDocument();
  });

  it('opens copyable apply command dialog from mount folder menu', async () => {
    const user = userEvent.setup();
    renderTree();

    fireEvent.contextMenu(screen.getByTitle('Shared'));
    await user.click(screen.getByTestId('host-mount-menu-apply-command'));

    await waitFor(() => {
      expect(screen.getByTestId('host-mount-command-text')).toHaveValue('apply-command');
    });
  });
});
