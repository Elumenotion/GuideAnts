import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '../../../../test/test-utils';
import userEvent from '@testing-library/user-event';
import '@testing-library/jest-dom';

// -----------------------------------------------------------------------------
// Local component under test & test-doubles
// -----------------------------------------------------------------------------
import { ContentFileContent } from '../ContentFileContent';

// Stub FileContents to avoid nested fetches/logic – we only verify it renders
// a placeholder so ContentFileContent can mount without hitting the network.
vi.mock('../FileContents', () => ({
  FileContents: () => <div data-testid="stub-file-contents">Stub File Contents</div>,
}));

// Stub HistoryDrawer to avoid ProjectContext dependency
vi.mock('../../history/HistoryDrawer', () => ({
  HistoryDrawer: () => <div data-testid="stub-history-drawer" />,
}));

// -----------------------------------------------------------------------------
// Shared API mocks – maintained inside a single object to avoid hoisting issues.
// -----------------------------------------------------------------------------
vi.mock('../../../../services/api', () => {
  const mocks = {
    getContentFile: vi.fn(),
    deleteContentFile: vi.fn(),
    getContentFileContent: vi.fn(),
    getContentFileMarkdownShadow: vi.fn(),
    getContentFileMarkdownContent: vi.fn(),
    uploadFiles: vi.fn(),
  } as const;
  // Expose for tests via globalThis to avoid hoisting/TDZ issues
  (globalThis as any).__apiMocks = mocks;
  return {
    api: {
      projects: mocks,
      utils: {},
    },
  };
});

vi.mock('../../../../services/documentServer', () => ({
  getDocumentServerCapabilities: vi.fn().mockResolvedValue({ enabled: false }),
  looksLikeDocumentServerFile: vi.fn(() => false),
}));

vi.mock('../../../common/MarkdownViewer', () => ({
  default: ({ text }: { text: string }) => <div data-testid="markdown-viewer">{text}</div>,
}));

vi.mock('../../../notebook/conversations/FullScreenEditor', () => ({
  default: ({
    onSave,
    onCancel,
    content,
  }: {
    onSave: (value: string) => void;
    onCancel: () => void;
    content: string;
  }) => (
    <div data-testid="fullscreen-editor">
      <span>{content}</span>
      <button type="button" onClick={() => onSave('# Saved markdown')}>
        Save markdown
      </button>
      <button type="button" onClick={onCancel}>
        Cancel markdown
      </button>
    </div>
  ),
}));

// Helper accessors after mocks initialised
const apiMocks = () => (globalThis as any).__apiMocks as {
  getContentFile: ReturnType<typeof vi.fn>;
  deleteContentFile: ReturnType<typeof vi.fn>;
  getContentFileContent: ReturnType<typeof vi.fn>;
  getContentFileMarkdownShadow: ReturnType<typeof vi.fn>;
  getContentFileMarkdownContent: ReturnType<typeof vi.fn>;
  uploadFiles: ReturnType<typeof vi.fn>;
};

const getContentFile = () => apiMocks().getContentFile;
const deleteContentFile = () => apiMocks().deleteContentFile;
const getContentFileContent = () => apiMocks().getContentFileContent;
const getContentFileMarkdownShadow = () => apiMocks().getContentFileMarkdownShadow;
const getContentFileMarkdownContent = () => apiMocks().getContentFileMarkdownContent;

// Re-usable DTO fixture
const baseFileDto = {
  id: 'f1',
  name: 'docs/readme.md',
  contentType: 'text/markdown',
  fileName: 'readme.md',
  index: false,
  documentId: '',
  created: new Date().toISOString(),
};

// Convenience helpers
const PROJECT_ID = 'p1';
const FILE_ID = 'f1';

function markdownBlob(text: string) {
  return {
    text: () => Promise.resolve(text),
  };
}

// -----------------------------------------------------------------------------
// Tests
// -----------------------------------------------------------------------------

describe('ContentFileContent', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('deletes the file after user confirmation', async () => {
    getContentFile().mockResolvedValueOnce(baseFileDto);
    deleteContentFile().mockResolvedValueOnce(undefined);

    const onDelete = vi.fn();

    render(
      <ContentFileContent
        projectId={PROJECT_ID}
        fileId={FILE_ID}
        canEdit
        onDelete={onDelete}
      />,
    );

    // Wait for file name to appear then click delete
    await screen.findByText(baseFileDto.fileName);

    await userEvent.click(screen.getByRole('button', { name: /delete/i }));

    // Click confirm in dialog
    const confirmButton = await screen.findByTestId('confirm');
    await userEvent.click(confirmButton);

    await waitFor(() => {
      expect(deleteContentFile()).toHaveBeenCalledWith(PROJECT_ID, FILE_ID);
      expect(onDelete).toHaveBeenCalledTimes(1);
    });
  });

  it('shows an error message when fetching file details fails', async () => {
    getContentFile().mockRejectedValueOnce(new Error('Fetch failed'));

    render(<ContentFileContent projectId={PROJECT_ID} fileId={FILE_ID} />);

    expect(
      await screen.findByText(/failed to load file details\. please try again\./i),
    ).toBeInTheDocument();
  });

  it('downloads the file when the download button is clicked', async () => {
    getContentFile().mockResolvedValueOnce(baseFileDto);
    getContentFileContent().mockResolvedValueOnce({
      blob: new Blob(['# Hello'], { type: 'text/markdown' }),
      fileName: 'readme.md',
    });

    const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    const createObjectUrlSpy = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:download');

    render(<ContentFileContent projectId={PROJECT_ID} fileId={FILE_ID} />);
    await screen.findByText(baseFileDto.fileName);

    await userEvent.click(screen.getByRole('button', { name: /download/i }));

    await waitFor(() => {
      expect(getContentFileContent()).toHaveBeenCalledWith(PROJECT_ID, FILE_ID, undefined);
      expect(createObjectUrlSpy).toHaveBeenCalled();
      expect(clickSpy).toHaveBeenCalled();
    });

    clickSpy.mockRestore();
    createObjectUrlSpy.mockRestore();
  });

  it('opens the history drawer from the header action', async () => {
    getContentFile().mockResolvedValueOnce(baseFileDto);

    render(<ContentFileContent projectId={PROJECT_ID} fileId={FILE_ID} />);
    await screen.findByText(baseFileDto.fileName);

    await userEvent.click(screen.getByRole('button', { name: /history/i }));
    expect(screen.getByTestId('stub-history-drawer')).toBeInTheDocument();
  });

  it('shows file-in-use dialog when delete is blocked', async () => {
    getContentFile().mockResolvedValueOnce(baseFileDto);
    deleteContentFile().mockRejectedValueOnce({
      isFileInUse: true,
      notebooksUsingFile: [{
        notebookId: 'nb-1',
        notebookTitle: 'Research',
        notebookFileId: 'nf-1',
        fileName: 'readme.md',
        relativePath: 'docs/readme.md',
      }],
    });

    const onDelete = vi.fn();
    render(
      <ContentFileContent
        projectId={PROJECT_ID}
        fileId={FILE_ID}
        canEdit
        onDelete={onDelete}
      />,
    );

    await screen.findByText(baseFileDto.fileName);
    await userEvent.click(screen.getByRole('button', { name: /delete/i }));
    await userEvent.click(await screen.findByTestId('confirm'));

    expect(await screen.findByText('Cannot Delete File')).toBeInTheDocument();
    expect(screen.getByText('Research')).toBeInTheDocument();
    expect(onDelete).not.toHaveBeenCalled();
  });

  it('renders extracted markdown tab content for PDF files', async () => {
    const pdfDto = {
      ...baseFileDto,
      fileName: 'report.pdf',
      contentType: 'application/pdf',
    };
    getContentFile().mockResolvedValue(pdfDto);
    getContentFileMarkdownShadow().mockResolvedValue({
      id: 'shadow-1',
      status: 'Completed',
      originalContentFileVersionId: 'v1',
      contentHash: 'hash',
      fileSize: 10,
      created: new Date().toISOString(),
    });
    getContentFileMarkdownContent().mockResolvedValue({
      blob: markdownBlob('Extracted text'),
    });

    render(<ContentFileContent projectId={PROJECT_ID} fileId={FILE_ID} />);
    await screen.findByText('report.pdf');

    await userEvent.click(screen.getByRole('button', { name: /extracted text/i }));

    expect(await screen.findByTestId('markdown-viewer')).toHaveTextContent('Extracted text');
  });

  it('renders markdown in hideHeader mode when extraction is complete', async () => {
    const docxDto = {
      ...baseFileDto,
      fileName: 'summary.docx',
      contentType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    };
    getContentFile().mockResolvedValue(docxDto);
    getContentFileMarkdownShadow().mockResolvedValue({
      id: 'shadow-2',
      status: 'Completed',
      originalContentFileVersionId: 'v1',
      contentHash: 'hash',
      fileSize: 10,
      created: new Date().toISOString(),
    });
    getContentFileMarkdownContent().mockResolvedValue({
      blob: markdownBlob('Home page markdown'),
    });

    render(<ContentFileContent projectId={PROJECT_ID} fileId={FILE_ID} hideHeader />);

    expect(await screen.findByTestId('markdown-viewer')).toHaveTextContent('Home page markdown');
    expect(screen.queryByRole('button', { name: /download/i })).not.toBeInTheDocument();
  });

  it('shows generic delete error when deletion fails for other reasons', async () => {
    getContentFile().mockResolvedValueOnce(baseFileDto);
    deleteContentFile().mockRejectedValueOnce(new Error('Server error'));

    render(
      <ContentFileContent projectId={PROJECT_ID} fileId={FILE_ID} canEdit onDelete={vi.fn()} />,
    );

    await screen.findByText(baseFileDto.fileName);
    await userEvent.click(screen.getByRole('button', { name: /delete/i }));
    await userEvent.click(await screen.findByTestId('confirm'));

    expect(await screen.findByText(/failed to delete file/i)).toBeInTheDocument();
  });

  it('shows failed extraction status on the extracted text tab', async () => {
    const pdfDto = { ...baseFileDto, fileName: 'broken.pdf', contentType: 'application/pdf' };
    getContentFile().mockResolvedValue(pdfDto);
    getContentFileMarkdownShadow().mockResolvedValue({
      id: 'shadow-fail',
      status: 'Failed',
      errorMessage: 'OCR unavailable',
      originalContentFileVersionId: 'v1',
      contentHash: 'hash',
      fileSize: 10,
      created: new Date().toISOString(),
    });

    render(<ContentFileContent projectId={PROJECT_ID} fileId={FILE_ID} />);
    await screen.findByText('broken.pdf');

    await waitFor(() => {
      expect(screen.getByText('Failed')).toBeInTheDocument();
    });
    expect(screen.getByRole('button', { name: /extracted text/i })).toBeDisabled();
  });

  it('opens markdown editor for markdown files', async () => {
    getContentFile().mockResolvedValue(baseFileDto);
    getContentFileMarkdownContent().mockResolvedValue({
      blob: markdownBlob('# Heading'),
    });

    render(<ContentFileContent projectId={PROJECT_ID} fileId={FILE_ID} />);
    await screen.findByText(baseFileDto.fileName);
    await userEvent.click(screen.getByRole('button', { name: /edit/i }));

    expect(await screen.findByTestId('fullscreen-editor')).toBeInTheDocument();
  });

  it('cancels delete confirmation without calling api', async () => {
    getContentFile().mockResolvedValueOnce(baseFileDto);

    render(
      <ContentFileContent projectId={PROJECT_ID} fileId={FILE_ID} canEdit onDelete={vi.fn()} />,
    );

    await screen.findByText(baseFileDto.fileName);
    await userEvent.click(screen.getByRole('button', { name: /delete/i }));
    await screen.findByText(/are you sure you want to delete/i);
    const cancelButtons = screen.getAllByRole('button', { name: /^cancel$/i });
    await userEvent.click(cancelButtons[cancelButtons.length - 1]);

    expect(deleteContentFile()).not.toHaveBeenCalled();
  });

  it('updates latestVersion when file-version-updated event fires', async () => {
    getContentFile().mockResolvedValue({
      ...baseFileDto,
      latestVersion: 1,
    });

    render(<ContentFileContent projectId={PROJECT_ID} fileId={FILE_ID} />);
    await screen.findByText(baseFileDto.fileName);

    window.dispatchEvent(
      new CustomEvent('file-version-updated', {
        detail: { fileId: FILE_ID, newVersion: 4 },
      }),
    );

    await waitFor(() => {
      expect(getContentFile()).toHaveBeenCalled();
    });
  });

  it('auto-switches to extracted text tab for non-previewable files', async () => {
    const docxDto = {
      ...baseFileDto,
      fileName: 'summary.docx',
      contentType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    };
    getContentFile().mockResolvedValue(docxDto);
    getContentFileMarkdownShadow().mockResolvedValue({
      id: 'shadow-auto',
      status: 'Completed',
      originalContentFileVersionId: 'v1',
      contentHash: 'hash',
      fileSize: 10,
      created: new Date().toISOString(),
    });

    render(<ContentFileContent projectId={PROJECT_ID} fileId={FILE_ID} />);
    await screen.findByText('summary.docx');

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /extracted text/i })).toHaveClass('bg-blue-600');
    });
  });

  it('shows pending extraction badge on the extracted text tab', async () => {
    const pdfDto = { ...baseFileDto, fileName: 'pending.pdf', contentType: 'application/pdf' };
    getContentFile().mockResolvedValue(pdfDto);
    getContentFileMarkdownShadow().mockResolvedValue({
      id: 'shadow-pending',
      status: 'Pending',
      originalContentFileVersionId: 'v1',
      contentHash: 'hash',
      fileSize: 10,
      created: new Date().toISOString(),
    });

    render(<ContentFileContent projectId={PROJECT_ID} fileId={FILE_ID} />);
    await screen.findByText('pending.pdf');

    expect(await screen.findByText('Pending')).toBeInTheDocument();
    await waitFor(() => {
      expect(screen.getByRole('button', { name: /extracted text/i })).toBeDisabled();
    });
  });

  it('shows download error when blob fetch fails', async () => {
    getContentFile().mockResolvedValue(baseFileDto);
    getContentFileContent().mockRejectedValueOnce(new Error('network'));

    render(<ContentFileContent projectId={PROJECT_ID} fileId={FILE_ID} />);
    await screen.findByText(baseFileDto.fileName);
    await userEvent.click(screen.getByRole('button', { name: /download/i }));

    expect(await screen.findByText(/failed to download file/i)).toBeInTheDocument();
  });

  it('falls back to original content when markdown endpoint fails for editor', async () => {
    getContentFile().mockResolvedValue(baseFileDto);
    getContentFileMarkdownContent().mockRejectedValueOnce(new Error('no markdown'));
    getContentFileContent().mockResolvedValue({
      blob: markdownBlob('# From original'),
    });

    render(<ContentFileContent projectId={PROJECT_ID} fileId={FILE_ID} />);
    await screen.findByText(baseFileDto.fileName);
    await userEvent.click(screen.getByRole('button', { name: /edit/i }));

    expect(await screen.findByTestId('fullscreen-editor')).toBeInTheDocument();
  });

  it('renders hideHeader file contents when extraction is not ready', async () => {
    const docxDto = {
      ...baseFileDto,
      fileName: 'draft.docx',
      contentType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    };
    getContentFile().mockResolvedValue(docxDto);
    getContentFileMarkdownShadow().mockResolvedValue({
      id: 'shadow-processing',
      status: 'Processing',
      originalContentFileVersionId: 'v1',
      contentHash: 'hash',
      fileSize: 10,
      created: new Date().toISOString(),
    });

    render(<ContentFileContent projectId={PROJECT_ID} fileId={FILE_ID} hideHeader />);
    expect(await screen.findByTestId('stub-file-contents')).toBeInTheDocument();
  });

  it('renders plain file view when markdown extraction is unsupported', async () => {
    const textDto = {
      ...baseFileDto,
      fileName: 'notes.txt',
      contentType: 'text/plain',
    };
    getContentFile().mockResolvedValue(textDto);

    render(<ContentFileContent projectId={PROJECT_ID} fileId={FILE_ID} />);
    await screen.findByText('notes.txt');
    expect(screen.getByTestId('stub-file-contents')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /extracted text/i })).not.toBeInTheDocument();
  });

  it('saves markdown from fullscreen editor and refreshes metadata', async () => {
    getContentFile().mockResolvedValue({ ...baseFileDto, latestVersion: 2 });
    getContentFileMarkdownContent().mockResolvedValue({
      blob: markdownBlob('# Heading'),
    });
    apiMocks().uploadFiles.mockResolvedValue([{ latestVersion: 3 }]);

    render(<ContentFileContent projectId={PROJECT_ID} fileId={FILE_ID} />);
    await screen.findByText(baseFileDto.fileName);
    await userEvent.click(screen.getByRole('button', { name: /edit/i }));
    await userEvent.click(screen.getByRole('button', { name: /save markdown/i }));

    await waitFor(() => {
      expect(apiMocks().uploadFiles).toHaveBeenCalled();
      expect(screen.queryByTestId('fullscreen-editor')).not.toBeInTheDocument();
    });
  });

  it('closes markdown editor without saving', async () => {
    getContentFile().mockResolvedValue(baseFileDto);
    getContentFileMarkdownContent().mockResolvedValue({
      blob: markdownBlob('# Heading'),
    });

    render(<ContentFileContent projectId={PROJECT_ID} fileId={FILE_ID} />);
    await screen.findByText(baseFileDto.fileName);
    await userEvent.click(screen.getByRole('button', { name: /edit/i }));
    await userEvent.click(screen.getByRole('button', { name: /cancel markdown/i }));

    expect(screen.queryByTestId('fullscreen-editor')).not.toBeInTheDocument();
  });

  it('shows skipped extraction badge when extraction is skipped', async () => {
    const pdfDto = { ...baseFileDto, fileName: 'scan.pdf', contentType: 'application/pdf' };
    getContentFile().mockResolvedValue(pdfDto);
    getContentFileMarkdownShadow().mockResolvedValue({
      id: 'shadow-skipped',
      status: 'Skipped',
      originalContentFileVersionId: 'v1',
      contentHash: 'hash',
      fileSize: 10,
      created: new Date().toISOString(),
    });

    render(<ContentFileContent projectId={PROJECT_ID} fileId={FILE_ID} />);
    await screen.findByText('scan.pdf');
    const extractedTab = await screen.findByRole('button', { name: /extracted text/i });
    expect(extractedTab).toBeDisabled();
    expect(await screen.findByText('Skipped')).toBeInTheDocument();
  });

  it('retries markdown fetch after an error', async () => {
    const docxDto = {
      ...baseFileDto,
      fileName: 'retry.docx',
      contentType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    };
    getContentFile().mockResolvedValue(docxDto);
    getContentFileMarkdownShadow().mockResolvedValue({
      id: 'shadow-retry',
      status: 'Completed',
      originalContentFileVersionId: 'v1',
      contentHash: 'hash',
      fileSize: 10,
      created: new Date().toISOString(),
    });
    getContentFileMarkdownContent()
      .mockRejectedValueOnce(new Error('fetch failed'))
      .mockResolvedValueOnce({ blob: markdownBlob('Recovered text') });

    render(<ContentFileContent projectId={PROJECT_ID} fileId={FILE_ID} />);
    await screen.findByText('retry.docx');
    await waitFor(() => {
      expect(screen.getByRole('button', { name: /extracted text/i })).toHaveClass('bg-blue-600');
    });

    expect(await screen.findByText(/failed to load markdown content/i)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: /retry/i }));
    expect(await screen.findByTestId('markdown-viewer')).toHaveTextContent('Recovered text');
  });
}); 