import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { screen, fireEvent, waitFor, act, renderWithNotebookRoute } from '../../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import { NotebookFolderTree } from '../NotebookFolderTree';
import { NotebookFileDto, NotebookFolderTreeDto, NotebookSidebarSelectedItem } from '../../../../types/notebook';
import type { NotebookHostMountEntry } from '../../../../types/hostFolderMount';
import { notebookFilesApi } from '../../../../services/notebookFiles';
import { isDocumentServerLive } from '../../../../services/documentServer';
import { useNotebookHostMounts } from '../../../../hooks/useNotebookHostMounts';

vi.mock('../../../../services/documentServer', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../../services/documentServer')>()),
  isDocumentServerLive: vi.fn(),
}));

vi.mock('../../../../services/notebookFiles', () => ({
  notebookFilesApi: {
    uploadFiles: vi.fn(),
    getNotebookFileMarkdownContent: vi.fn(),
    getNotebookFileContent: vi.fn(),
  },
}));

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

vi.mock('../../../notebook/conversations/FullScreenEditor', () => ({
  default: () => null,
}));

const DOCX = 'application/vnd.openxmlformats-officedocument.wordprocessingml.document';
const XLSX = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';
const PPTX = 'application/vnd.openxmlformats-officedocument.presentationml.presentation';
const OFFICE_LABELS = ['New Word Document', 'New Excel Spreadsheet', 'New PowerPoint Presentation'];

const notebookFile = (fileName: string, relativePath: string): NotebookFileDto => ({
  id: `id-${relativePath}`,
  fileName,
  relativePath,
  fileSize: 10,
  lastModifiedUtc: '2024-01-01T00:00:00Z',
  fileHash: `hash-${relativePath}`,
  isIndexed: false,
  index: false,
});

const tree: NotebookFolderTreeDto = {
  name: 'Notebook',
  relativePath: '',
  subFolders: [
    {
      name: 'Docs',
      relativePath: 'Docs',
      subFolders: [],
      files: [notebookFile('New Presentation.pptx', 'Docs/New Presentation.pptx')],
    },
    { name: 'Shared', relativePath: 'Shared', subFolders: [], files: [] },
  ],
  files: [],
};

const linkedMount: NotebookHostMountEntry = {
  mountId: 'mount-linked',
  leafName: 'Shared',
  relativePath: 'Shared',
  displayName: 'Shared data',
  displayState: 'Linked',
  mountStatus: 'Active',
  scope: 'Notebook',
  linkStatus: 'Linked',
};

const renderTree = (overrides: Partial<React.ComponentProps<typeof NotebookFolderTree>> = {}) => {
  const props = {
    tree,
    notebookName: 'Test Notebook',
    selectedItem: null as NotebookSidebarSelectedItem | null,
    onItemSelect: vi.fn(),
    onCreateFolder: vi.fn().mockResolvedValue(undefined),
    onRenameFolder: vi.fn().mockResolvedValue(undefined),
    onDeleteFolder: vi.fn().mockResolvedValue(undefined),
    onUploadToFolder: vi.fn(),
    onPreviewFile: vi.fn(),
    canEdit: true,
    activeSection: 'notebookFiles' as const,
    onSectionActivate: vi.fn(),
    ...overrides,
  };
  const result = renderWithNotebookRoute(<NotebookFolderTree {...props} />, {
    route: '/projects/proj-1/notebooks/nb-1',
    projectId: 'proj-1',
    notebookId: 'nb-1',
  });
  return { ...result, props };
};

const menuLabels = () =>
  Array.from(document.body.querySelectorAll('[data-tour-id="notebook.folder.context-menu"] button')).map(
    (b) => b.textContent
  );

const findPortalButton = async (label: string | RegExp) =>
  waitFor(() => {
    const match = Array.from(document.body.querySelectorAll('button')).find((b) =>
      typeof label === 'string' ? b.textContent === label : label.test(b.textContent ?? '')
    );
    if (!match) throw new Error(`Button not found: ${label}`);
    return match;
  });

const flushChecks = () => act(async () => {});

describe('NotebookFolderTree new Office documents', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(isDocumentServerLive).mockResolvedValue(true);
    vi.mocked(useNotebookHostMounts).mockReturnValue({
      mounts: [],
      isLoading: false,
      error: null,
      refresh: vi.fn().mockResolvedValue(undefined),
    });
    vi.mocked(notebookFilesApi.uploadFiles).mockResolvedValue([]);
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok: true,
        status: 200,
        headers: new Headers({ 'content-type': 'application/octet-stream' }),
        blob: async () => new Blob(['PK-template']),
      })
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  describe('visibility', () => {
    it('lists the three items directly below New Markdown File when DocumentServer is live', async () => {
      renderTree();

      fireEvent.contextMenu(screen.getByTitle('Docs'));

      await findPortalButton('New Word Document');
      const labels = menuLabels();
      const markdownIndex = labels.indexOf('New Markdown File');
      expect(labels.slice(markdownIndex, markdownIndex + 4)).toEqual(['New Markdown File', ...OFFICE_LABELS]);
    });

    it('hides the items when DocumentServer is not live', async () => {
      vi.mocked(isDocumentServerLive).mockResolvedValue(false);
      renderTree();

      fireEvent.contextMenu(screen.getByTitle('Docs'));
      await findPortalButton('New Markdown File');
      await flushChecks();

      for (const label of OFFICE_LABELS) expect(screen.queryByText(label)).not.toBeInTheDocument();
    });

    it('hides the items and skips the check without edit rights', async () => {
      renderTree({ canEdit: false });

      fireEvent.contextMenu(screen.getByTitle('Docs'));
      await findPortalButton('Copy path');
      await flushChecks();

      for (const label of OFFICE_LABELS) expect(screen.queryByText(label)).not.toBeInTheDocument();
      expect(isDocumentServerLive).not.toHaveBeenCalled();
    });

    it('hides the items and skips the check on a host-mounted folder', async () => {
      vi.mocked(useNotebookHostMounts).mockReturnValue({
        mounts: [linkedMount],
        isLoading: false,
        error: null,
        refresh: vi.fn().mockResolvedValue(undefined),
      });
      renderTree();

      fireEvent.contextMenu(screen.getByTitle('Shared'));
      await findPortalButton('Copy path');
      await flushChecks();

      for (const label of OFFICE_LABELS) expect(screen.queryByText(label)).not.toBeInTheDocument();
      expect(isDocumentServerLive).not.toHaveBeenCalled();
    });

    it('hides the items in the multi-select menu', async () => {
      renderTree();

      fireEvent.keyDown(window, { key: 'a', ctrlKey: true });
      fireEvent.contextMenu(screen.getByTitle('Docs'));
      await findPortalButton(/Download \d+ Items?/);
      await flushChecks();

      for (const label of OFFICE_LABELS) expect(screen.queryByText(label)).not.toBeInTheDocument();
    });

    it('ignores a slow check from an earlier menu open', async () => {
      let resolveFirst!: (live: boolean) => void;
      vi.mocked(isDocumentServerLive)
        .mockImplementationOnce(() => new Promise<boolean>((resolve) => { resolveFirst = resolve; }))
        .mockResolvedValueOnce(false);
      renderTree();

      fireEvent.contextMenu(screen.getByTitle('Docs'));
      fireEvent.contextMenu(screen.getByTitle('Docs'));
      await flushChecks();
      await act(async () => { resolveFirst(true); });

      expect(screen.queryByText('New Word Document')).not.toBeInTheDocument();
    });
  });

  describe('creating files', () => {
    it('creates a de-duplicated PowerPoint in the folder without indexing, then previews it', async () => {
      const user = userEvent.setup();
      const created = notebookFile('New Presentation (2).pptx', 'Docs/New Presentation (2).pptx');
      vi.mocked(notebookFilesApi.uploadFiles).mockResolvedValue([created]);
      const { props } = renderTree();

      fireEvent.contextMenu(screen.getByTitle('Docs'));
      await user.click(await findPortalButton('New PowerPoint Presentation'));

      await waitFor(() => expect(props.onPreviewFile).toHaveBeenCalledWith(created));
      const [projectId, notebookId, files, targetPath, index] = vi.mocked(notebookFilesApi.uploadFiles).mock.calls[0];
      expect([projectId, notebookId, targetPath, index]).toEqual(['proj-1', 'nb-1', 'Docs', false]);
      expect(files[0].name).toBe('New Presentation (2).pptx');
      expect(files[0].type).toBe(PPTX);
    });

    it('creates at the notebook root with an empty target path', async () => {
      const user = userEvent.setup();
      renderTree();

      fireEvent.contextMenu(screen.getByTitle('Test Notebook'));
      await user.click(await findPortalButton('New Word Document'));

      await waitFor(() => expect(notebookFilesApi.uploadFiles).toHaveBeenCalled());
      const [, , files, targetPath] = vi.mocked(notebookFilesApi.uploadFiles).mock.calls[0];
      expect(targetPath).toBe('');
      expect(files[0].name).toBe('New Document.docx');
      expect(files[0].type).toBe(DOCX);
    });

    it('opens the file the server returned when it was renamed', async () => {
      const user = userEvent.setup();
      const renamed = notebookFile('New Spreadsheet-1.xlsx', 'Docs/New Spreadsheet-1.xlsx');
      vi.mocked(notebookFilesApi.uploadFiles).mockResolvedValue([renamed]);
      const { props } = renderTree();

      fireEvent.contextMenu(screen.getByTitle('Docs'));
      await user.click(await findPortalButton('New Excel Spreadsheet'));

      await waitFor(() => expect(props.onPreviewFile).toHaveBeenCalledWith(renamed));
      const [, , files] = vi.mocked(notebookFilesApi.uploadFiles).mock.calls[0];
      expect(files[0].type).toBe(XLSX);
    });

    it('asks the notebook file list to refresh', async () => {
      const user = userEvent.setup();
      const refresh = vi.fn();
      window.addEventListener('refresh-notebook-files', refresh);
      renderTree();

      fireEvent.contextMenu(screen.getByTitle('Docs'));
      await user.click(await findPortalButton('New Word Document'));

      await waitFor(() => expect(refresh).toHaveBeenCalled());
      window.removeEventListener('refresh-notebook-files', refresh);
    });

    it('shows an error toast and previews nothing when the upload fails', async () => {
      const user = userEvent.setup();
      vi.spyOn(console, 'error').mockImplementation(() => {});
      vi.mocked(notebookFilesApi.uploadFiles).mockRejectedValue(new Error('500'));
      const { props } = renderTree();

      fireEvent.contextMenu(screen.getByTitle('Docs'));
      await user.click(await findPortalButton('New Excel Spreadsheet'));

      expect(await screen.findByText("Couldn't create Excel spreadsheet")).toBeInTheDocument();
      expect(props.onPreviewFile).not.toHaveBeenCalled();
    });
  });
});
