import React from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, act } from '../../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';
import { FolderTree } from '../FolderTree';
import { FolderTreeDto, ProjectContentFile, ProjectFolderDto } from '../../../../types/project';
import { isDocumentServerLive } from '../../../../services/documentServer';

vi.mock('../../../../services/documentServer', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../../services/documentServer')>()),
  isDocumentServerLive: vi.fn(),
}));

vi.mock('../../../../services/api', () => {
  const mocks = {
    getContentFileContent: vi.fn(),
    uploadFiles: vi.fn(),
    // Expanding a mapped folder lazily lists it; never settle so the DTO children stay put.
    folders: { listHostMountLevel: vi.fn(() => new Promise(() => {})) },
  };
  (globalThis as { __officeTreeApiMocks?: typeof mocks }).__officeTreeApiMocks = mocks;
  return { api: { projects: mocks } };
});

vi.mock('../../../notebook/conversations/FullScreenEditor', () => ({
  default: () => null,
}));

const uploadFiles = () =>
  (globalThis as unknown as { __officeTreeApiMocks: { uploadFiles: ReturnType<typeof vi.fn> } }).__officeTreeApiMocks
    .uploadFiles;

const DOCX = 'application/vnd.openxmlformats-officedocument.wordprocessingml.document';
const XLSX = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';
const PPTX = 'application/vnd.openxmlformats-officedocument.presentationml.presentation';
const OFFICE_LABELS = ['New Word Document', 'New Excel Spreadsheet', 'New PowerPoint Presentation'];

const existingDoc: ProjectContentFile = {
  id: 'file-doc',
  fileName: 'New Document.docx',
  path: '/New Document.docx',
  relativePath: 'New Document.docx',
  contentType: DOCX,
  index: false,
  documentId: 'doc-1',
  created: '2023-01-01T00:00:00Z',
  fileSize: 100,
};

const tree: FolderTreeDto = {
  id: undefined,
  name: 'Project',
  relativePath: '',
  subFolders: [
    { id: 'folder-docs', name: 'Docs', relativePath: 'Docs', subFolders: [], files: [] },
    { id: 'folder-linked', name: 'Shortcut', relativePath: 'Shortcut', isLinked: true, subFolders: [], files: [] },
    {
      id: 'folder-mapped',
      name: 'Mapped',
      relativePath: 'Mapped',
      isHostMount: true,
      mountId: 'mount-1',
      subFolders: [{ id: 'folder-mapped-child', name: 'Child', relativePath: 'Mapped/Child', subFolders: [], files: [] }],
      files: [],
    },
  ],
  files: [existingDoc],
};

const folders: ProjectFolderDto[] = [
  { id: 'folder-docs', name: 'Docs', relativePath: 'Docs', projectId: 'proj', created: '' } as ProjectFolderDto,
];

const renderTree = (overrides: Partial<React.ComponentProps<typeof FolderTree>> = {}) => {
  const props = {
    folderTree: tree,
    folders,
    projectId: 'proj',
    onFileSelect: vi.fn(),
    onFolderSelect: vi.fn(),
    onCreateFolder: vi.fn().mockResolvedValue(undefined),
    onRenameFolder: vi.fn().mockResolvedValue(undefined),
    onDeleteFolder: vi.fn().mockResolvedValue(undefined),
    onUploadToFolder: vi.fn(),
    activeSection: 'contentFiles' as const,
    onSectionActivate: vi.fn(),
    ...overrides,
  };
  return { ...render(<FolderTree {...props} />), props };
};

const menuLabels = () =>
  Array.from(document.body.querySelectorAll('[data-tour-id="folder.context-menu"] button')).map((b) => b.textContent);

const findPortalButton = async (label: string | RegExp) =>
  waitFor(() => {
    const match = Array.from(document.body.querySelectorAll('button')).find((b) =>
      typeof label === 'string' ? b.textContent === label : label.test(b.textContent ?? '')
    );
    if (!match) throw new Error(`Button not found: ${label}`);
    return match;
  });

const expandFolder = (name: string) => {
  const row = screen.getByText(name).closest('.folder-tree-item');
  const toggle = row?.querySelector('button');
  if (toggle) fireEvent.click(toggle);
};

const flushChecks = () => act(async () => {});

describe('FolderTree new Office documents', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(isDocumentServerLive).mockResolvedValue(true);
    uploadFiles().mockResolvedValue([]);
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

      fireEvent.contextMenu(screen.getByText('Docs'));

      await findPortalButton('New Word Document');
      const labels = menuLabels();
      const markdownIndex = labels.indexOf('New Markdown File');
      expect(labels.slice(markdownIndex, markdownIndex + 4)).toEqual(['New Markdown File', ...OFFICE_LABELS]);
    });

    it('hides the items when DocumentServer is not live', async () => {
      vi.mocked(isDocumentServerLive).mockResolvedValue(false);
      renderTree();

      fireEvent.contextMenu(screen.getByText('Docs'));
      await findPortalButton('New Markdown File');
      await flushChecks();

      for (const label of OFFICE_LABELS) expect(screen.queryByText(label)).not.toBeInTheDocument();
    });

    it('hides the items inside a mapped host folder but keeps New Markdown File', async () => {
      renderTree();
      expandFolder('Mapped');

      fireEvent.contextMenu(await screen.findByText('Child'));
      await findPortalButton('New Markdown File');
      await flushChecks();

      for (const label of OFFICE_LABELS) expect(screen.queryByText(label)).not.toBeInTheDocument();
      expect(isDocumentServerLive).not.toHaveBeenCalled();
    });

    it('hides the items on a mapped folder root and a linked folder', async () => {
      renderTree();

      fireEvent.contextMenu(screen.getByText('Mapped'));
      await findPortalButton('Copy path');
      await flushChecks();
      for (const label of OFFICE_LABELS) expect(screen.queryByText(label)).not.toBeInTheDocument();

      fireEvent.contextMenu(screen.getByText('Shortcut'));
      await findPortalButton('Rename');
      await flushChecks();
      for (const label of OFFICE_LABELS) expect(screen.queryByText(label)).not.toBeInTheDocument();

      expect(isDocumentServerLive).not.toHaveBeenCalled();
    });

    it('hides the items in the multi-select menu', async () => {
      renderTree();

      fireEvent.keyDown(window, { key: 'a', ctrlKey: true });
      fireEvent.contextMenu(screen.getByText('Docs'));
      await findPortalButton(/Download \d+ Items/);
      await flushChecks();

      for (const label of OFFICE_LABELS) expect(screen.queryByText(label)).not.toBeInTheDocument();
    });

    it('does not open a menu or check DocumentServer when the tree is disabled', async () => {
      renderTree({ disabled: true });

      fireEvent.contextMenu(screen.getByText('Docs'));
      await flushChecks();

      expect(screen.queryByText('New Markdown File')).not.toBeInTheDocument();
      expect(isDocumentServerLive).not.toHaveBeenCalled();
    });

    it('ignores a slow check from an earlier menu open', async () => {
      let resolveFirst!: (live: boolean) => void;
      vi.mocked(isDocumentServerLive)
        .mockImplementationOnce(() => new Promise<boolean>((resolve) => { resolveFirst = resolve; }))
        .mockResolvedValueOnce(false);
      renderTree();

      fireEvent.contextMenu(screen.getByText('Docs'));
      fireEvent.contextMenu(screen.getByText('Docs'));
      await flushChecks();
      await act(async () => { resolveFirst(true); });

      expect(screen.queryByText('New Word Document')).not.toBeInTheDocument();
    });
  });

  describe('creating files', () => {
    it('creates a Word document in the folder and opens it', async () => {
      const user = userEvent.setup();
      uploadFiles().mockResolvedValue([
        { id: 'created-1', fileName: 'New Document.docx', relativePath: 'Docs/New Document.docx', contentType: DOCX },
      ]);
      const { props } = renderTree();

      fireEvent.contextMenu(screen.getByText('Docs'));
      await user.click(await findPortalButton('New Word Document'));

      await waitFor(() => expect(props.onFileSelect).toHaveBeenCalledWith('created-1'));
      const [projectId, files, folderId] = uploadFiles().mock.calls[0];
      expect(projectId).toBe('proj');
      expect(folderId).toBe('folder-docs');
      expect(files).toHaveLength(1);
      expect(files[0].name).toBe('New Document.docx');
      expect(files[0].type).toBe(DOCX);
    });

    it.each([
      ['New Excel Spreadsheet', 'New Spreadsheet.xlsx', XLSX],
      ['New PowerPoint Presentation', 'New Presentation.pptx', PPTX],
    ])('%s uploads %s with the right type', async (label, fileName, contentType) => {
      const user = userEvent.setup();
      renderTree();

      fireEvent.contextMenu(screen.getByText('Docs'));
      await user.click(await findPortalButton(label));

      await waitFor(() => expect(uploadFiles()).toHaveBeenCalled());
      const [, files] = uploadFiles().mock.calls[0];
      expect(files[0].name).toBe(fileName);
      expect(files[0].type).toBe(contentType);
    });

    it('creates at the project root with no folder id and a de-duplicated name', async () => {
      const user = userEvent.setup();
      renderTree();

      fireEvent.contextMenu(screen.getByText('Project'));
      await user.click(await findPortalButton('New Word Document'));

      await waitFor(() => expect(uploadFiles()).toHaveBeenCalled());
      const [, files, folderId] = uploadFiles().mock.calls[0];
      expect(folderId).toBeUndefined();
      expect(files[0].name).toBe('New Document (2).docx');
    });

    it('refreshes but does not open anything when the upload returns no file', async () => {
      const user = userEvent.setup();
      const refresh = vi.fn();
      window.addEventListener('refresh-project-files', refresh);
      uploadFiles().mockResolvedValue([]);
      const { props } = renderTree();

      fireEvent.contextMenu(screen.getByText('Docs'));
      await user.click(await findPortalButton('New Word Document'));

      await waitFor(() => expect(refresh).toHaveBeenCalled());
      expect(props.onFileSelect).not.toHaveBeenCalled();
      window.removeEventListener('refresh-project-files', refresh);
    });

    it('shows an error toast and opens nothing when the upload fails', async () => {
      const user = userEvent.setup();
      vi.spyOn(console, 'error').mockImplementation(() => {});
      uploadFiles().mockRejectedValue(new Error('500'));
      const { props } = renderTree();

      fireEvent.contextMenu(screen.getByText('Docs'));
      await user.click(await findPortalButton('New PowerPoint Presentation'));

      expect(await screen.findByText("Couldn't create PowerPoint presentation")).toBeInTheDocument();
      expect(props.onFileSelect).not.toHaveBeenCalled();
    });
  });
});
