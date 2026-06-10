import React from 'react';
import { render, screen, fireEvent, waitFor, within, act } from '../../../../test/test-utils';
import { describe, it, expect, beforeEach, vi, Mock } from 'vitest';
import { FilePreviewOverlay } from '../FilePreviewOverlay';
import { NotebookFileDto } from '../../../../types/notebook';
import { notebookFilesApi } from '../../../../services/notebookFiles';
import { getDocumentServerCapabilities, isDocumentServerSupportedByExtension, looksLikeDocumentServerFile } from '../../../../services/documentServer';
import { MarkdownExtractionStatus } from '../../../../types/api';
import { resolveHtmlResources } from '../../../../utils/htmlResourceResolver';

// Mock the services
vi.mock('../../../../services/notebookFiles', () => ({
  notebookFilesApi: {
    getNotebookFileContent: vi.fn(),
    getNotebookFileMarkdownShadow: vi.fn().mockResolvedValue({ status: 'Skipped' }),
    getNotebookFileMarkdownContent: vi.fn().mockResolvedValue({ blob: new Blob([''], { type: 'text/markdown' }) }),
    uploadFiles: vi.fn().mockResolvedValue([]),
  },
}));

vi.mock('../../conversations/FullScreenEditor', () => ({
  default: ({
    onSave,
    onCancel,
    content,
  }: {
    onSave: (value: string) => void;
    onCancel: () => void;
    content: string;
  }) => (
    <div data-testid="fullscreen-md-editor">
      <span>{content}</span>
      <button type="button" onClick={() => onSave('# Updated markdown')}>
        Save markdown
      </button>
      <button type="button" onClick={onCancel}>
        Cancel markdown
      </button>
    </div>
  ),
}));

vi.mock('../../../../services/documentServer', () => ({
  getDocumentServerCapabilities: vi.fn(),
  isDocumentServerSupportedByExtension: vi.fn(),
  looksLikeDocumentServerFile: vi.fn(() => false),
}));

vi.mock('../../../../utils/htmlResourceResolver', () => ({
  resolveHtmlResources: vi.fn(),
  cleanupBlobUrls: vi.fn(),
}));

vi.mock('../../../common/ConfirmationDialog', () => ({
  ConfirmationDialog: ({
    isOpen,
    message,
    onConfirm,
  }: {
    isOpen: boolean;
    message: string;
    onConfirm: () => void;
  }) =>
    isOpen ? (
      <div role="dialog">
        <p>{message}</p>
        <button type="button" onClick={onConfirm}>
          OK
        </button>
      </div>
    ) : null,
}));

vi.mock('../../../common/DocumentServerEditor', () => ({
  default: ({ showErrorDialogOnError }: { showErrorDialogOnError?: boolean }) => (
    <div
      data-testid="documentserver-editor"
      data-show-error-dialog-on-error={String(showErrorDialogOnError)}
    >
      DocumentServer
    </div>
  ),
}));

// Mock the viewer components
vi.mock('../../../common/ImageViewer', () => ({
  ImageViewer: ({ src, alt }: { src: string; alt: string }) => (
    <div data-testid="image-viewer">
      <img src={src} alt={alt} />
    </div>
  ),
}));

vi.mock('../../../common/PdfViewer', () => ({
  default: ({ blob }: { blob: Blob }) => (
    <div data-testid="pdf-viewer">PDF Viewer - Size: {blob.size}</div>
  ),
}));

vi.mock('../../../common/MarkdownViewer', () => ({
  default: ({ text, className }: { text: string; className: string }) => (
    <div data-testid="markdown-viewer" className={className}>
      {text}
    </div>
  ),
}));

vi.mock('../../../common/TextViewer', () => ({
  default: ({ text }: { text: string }) => (
    <div data-testid="text-viewer">{text}</div>
  ),
}));

vi.mock('../../../common/VideoPlayer', () => ({
  default: ({ blob }: { blob: Blob }) => (
    <div data-testid="video-player">Video Player - Size: {blob.size}</div>
  ),
}));

vi.mock('../../../common/AudioPlayer', () => ({
  default: ({ blob }: { blob: Blob }) => (
    <div data-testid="audio-player">Audio Player - Size: {blob.size}</div>
  ),
}));

// Mock URL.createObjectURL and revokeObjectURL
const mockCreateObjectURL = vi.fn();
const mockRevokeObjectURL = vi.fn();

Object.defineProperty(window.URL, 'createObjectURL', {
  writable: true,
  value: mockCreateObjectURL,
});

Object.defineProperty(window.URL, 'revokeObjectURL', {
  writable: true,
  value: mockRevokeObjectURL,
});

describe('FilePreviewOverlay', () => {
  const defaultProps = {
    projectId: 'project-1',
    notebookId: 'notebook-1',
    onClose: vi.fn(),
  };

  const mockFile: NotebookFileDto = {
    id: 'file-1',
    fileName: 'test-file.txt',
    relativePath: 'test-file.txt',
    fileSize: 1024,
    lastModifiedUtc: '2023-01-01T00:00:00Z',
    fileHash: 'hash123',
    isIndexed: false,
  };

  // Helper function to create a blob mock that properly handles text content
  const createMockBlob = (content: string, type: string = 'text/plain') => {
    const mockBlob = {
      size: content.length,
      type: type,
      text: async () => content,
      stream: () => {},
      arrayBuffer: async () => new ArrayBuffer(0),
      slice: () => mockBlob
    };
    return mockBlob as Blob;
  };



  const mockTextBlob = createMockBlob('Hello World');
  const mockImageBlob = createMockBlob('fake-image-data', 'image/png');
  const mockPdfBlob = createMockBlob('fake-pdf-data', 'application/pdf');

  beforeEach(() => {
    vi.clearAllMocks();
    mockCreateObjectURL.mockReturnValue('mock-object-url');
    (getDocumentServerCapabilities as Mock).mockResolvedValue({
      enabled: false,
      publicUrl: '',
      supportedExtensions: [],
      supportedContentTypes: [],
    });
    (isDocumentServerSupportedByExtension as Mock).mockReturnValue(false);
    (looksLikeDocumentServerFile as Mock).mockReturnValue(false);
    (resolveHtmlResources as Mock).mockResolvedValue({
      html: '<html><body>Resolved HTML</body></html>',
      blobUrls: new Set<string>(),
      discoveredRoot: 'site',
    });
  });

  describe('Component Rendering', () => {
    it('shows loading state initially', () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockImplementation(
        () => new Promise(() => {}) // Never resolves
      );

      render(<FilePreviewOverlay {...defaultProps} file={mockFile} />);

      expect(screen.getByText('Loading file content...')).toBeInTheDocument();
    });

    it('applies correct modal classes for large content', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockImageBlob);
      const imageFile = { ...mockFile, fileName: 'image.png' };

      render(<FilePreviewOverlay {...defaultProps} file={imageFile} />);

      await waitFor(() => {
        expect(screen.getByTestId('image-viewer')).toBeInTheDocument();
      });

      const modal = document.querySelector('[class*="max-w-4xl"]');
      expect(modal).toBeTruthy();
    });

    it('applies correct modal classes for small content', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(
        createMockBlob('data', 'application/octet-stream')
      );
      const unknownFile = { ...mockFile, fileName: 'unknown.xyz' };

      render(<FilePreviewOverlay {...defaultProps} file={unknownFile} />);

      await waitFor(() => {
        expect(screen.getByText('No preview available')).toBeInTheDocument();
      });

      const modal2 = document.querySelector('[class*="max-w-md"]');
      expect(modal2).toBeTruthy();
    });
  });

  describe('File Type Detection', () => {
    it('detects PDF files correctly', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockPdfBlob);
      const pdfFile = { ...mockFile, fileName: 'document.pdf' };

      render(<FilePreviewOverlay {...defaultProps} file={pdfFile} />);

      await waitFor(() => {
        expect(screen.getByTestId('pdf-viewer')).toBeInTheDocument();
      });
    });

    it('detects image files correctly', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockImageBlob);
      const imageFile = { ...mockFile, fileName: 'image.jpg' };

      render(<FilePreviewOverlay {...defaultProps} file={imageFile} />);

      await waitFor(() => {
        expect(screen.getByTestId('image-viewer')).toBeInTheDocument();
      });
    });

    it('detects text files correctly', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockTextBlob);

      render(<FilePreviewOverlay {...defaultProps} file={mockFile} />);

      await waitFor(() => {
        expect(screen.getByTestId('text-viewer')).toBeInTheDocument();
      });
    });

    it('detects markdown files correctly', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(
        createMockBlob('# Markdown', 'text/plain')
      );
      const markdownFile = { ...mockFile, fileName: 'readme.md' };

      render(<FilePreviewOverlay {...defaultProps} file={markdownFile} />);

      await waitFor(() => {
        expect(screen.getByTestId('markdown-viewer')).toBeInTheDocument();
      });
    });

    it('detects JSON files correctly', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(
        createMockBlob('{"key": "value"}', 'application/json')
      );
      const jsonFile = { ...mockFile, fileName: 'data.json' };

      render(<FilePreviewOverlay {...defaultProps} file={jsonFile} />);

      await waitFor(() => {
        expect(screen.getByTestId('text-viewer')).toBeInTheDocument();
      });
    });

    it('detects TypeScript files correctly', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(
        createMockBlob('const x: string = "hello";', 'application/typescript')
      );
      const tsFile = { ...mockFile, fileName: 'code.ts' };

      render(<FilePreviewOverlay {...defaultProps} file={tsFile} />);

      await waitFor(() => {
        expect(screen.getByTestId('text-viewer')).toBeInTheDocument();
      });
    });

    it('detects video files correctly', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(
        createMockBlob('fake-video', 'video/mp4')
      );
      const videoFile = { ...mockFile, fileName: 'video.mp4' };

      render(<FilePreviewOverlay {...defaultProps} file={videoFile} />);

      await waitFor(() => {
        expect(screen.getByTestId('video-player')).toBeInTheDocument();
      });
    });

    it('detects audio files correctly', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(
        createMockBlob('fake-audio', 'audio/mp3')
      );
      const audioFile = { ...mockFile, fileName: 'audio.mp3' };

      render(<FilePreviewOverlay {...defaultProps} file={audioFile} />);

      await waitFor(() => {
        expect(screen.getByTestId('audio-player')).toBeInTheDocument();
      });
    });
  });

  describe('Content Loading', () => {
    it('loads file content on mount', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockTextBlob);

      render(<FilePreviewOverlay {...defaultProps} file={mockFile} />);

      expect(notebookFilesApi.getNotebookFileContent).toHaveBeenCalledWith(
        'project-1',
        'notebook-1',
        'test-file.txt',
        'hash123'
      );

      await waitFor(() => {
        expect(screen.getByTestId('text-viewer')).toBeInTheDocument();
      });
    });

    it('reloads content when file changes', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockTextBlob);

      const { rerender } = render(<FilePreviewOverlay {...defaultProps} file={mockFile} />);

      await waitFor(() => {
        expect(screen.getByTestId('text-viewer')).toBeInTheDocument();
      });

      const newFile = { ...mockFile, id: 'file-2', fileName: 'new-file.txt' };
      rerender(<FilePreviewOverlay {...defaultProps} file={newFile} />);

      expect(notebookFilesApi.getNotebookFileContent).toHaveBeenCalledWith(
        'project-1',
        'notebook-1',
        'test-file.txt',
        'hash123'
      );
    });

    it('processes text content for text files', async () => {
      const textContent = 'Hello World Content';
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(
        createMockBlob(textContent, 'text/plain')
      );

      render(<FilePreviewOverlay {...defaultProps} file={mockFile} />);

      await waitFor(() => {
        expect(screen.getByText(textContent)).toBeInTheDocument();
      });
    });

    it('shows processing text content message', async () => {
      // Create a mock that delays the blob.text() call
      const mockBlob = {
        text: vi.fn().mockImplementation(() => new Promise(() => {})), // Never resolves
      } as any;
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockBlob);

      render(<FilePreviewOverlay {...defaultProps} file={mockFile} />);

      await waitFor(() => {
        expect(screen.getByText('Processing text content...')).toBeInTheDocument();
      });
    });
  });

  describe('Error Handling', () => {
    it('displays error when file loading fails', async () => {
      const errorMessage = 'Failed to load file';
      (notebookFilesApi.getNotebookFileContent as Mock).mockRejectedValue(
        new Error(errorMessage)
      );

      render(<FilePreviewOverlay {...defaultProps} file={mockFile} />);

      const dialog = await screen.findByRole('dialog');
      expect(within(dialog).getByText(errorMessage)).toBeInTheDocument();
      expect(screen.getAllByText(errorMessage).length).toBeGreaterThanOrEqual(2);
    });

    it('displays generic error message when error has no message', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockRejectedValue(new Error());

      render(<FilePreviewOverlay {...defaultProps} file={mockFile} />);

      const message = 'Failed to load file content.';
      const dialog = await screen.findByRole('dialog');
      expect(within(dialog).getByText(message)).toBeInTheDocument();
      expect(screen.getAllByText(message).length).toBeGreaterThanOrEqual(2);
    });

    it('handles text content reading errors', async () => {
      const mockBlob = {
        text: vi.fn().mockRejectedValue(new Error('Text reading failed')),
      } as any;
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockBlob);

      // Spy on console.error to verify error logging
      const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});

      render(<FilePreviewOverlay {...defaultProps} file={mockFile} />);

      const message = 'Failed to read text content. Please try again.';
      const dialog = await screen.findByRole('dialog');
      expect(within(dialog).getByText(message)).toBeInTheDocument();
      expect(screen.getAllByText(message).length).toBeGreaterThanOrEqual(2);

      expect(consoleSpy).toHaveBeenCalledWith('Failed to read text content:', expect.any(Error));
      consoleSpy.mockRestore();
    });
  });

  describe('User Interactions', () => {
    it('calls onClose when overlay background is clicked', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockTextBlob);

      render(<FilePreviewOverlay {...defaultProps} file={mockFile} />);

      const overlay = screen.getByText('test-file.txt').closest('.fixed');
      fireEvent.click(overlay!);

      expect(defaultProps.onClose).toHaveBeenCalledTimes(1);
    });

    it('does not close when modal content is clicked', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockTextBlob);

      render(<FilePreviewOverlay {...defaultProps} file={mockFile} />);

      await waitFor(() => {
        expect(screen.getByTestId('text-viewer')).toBeInTheDocument();
      });

      const modalContent = screen.getByRole('main');
      fireEvent.click(modalContent!);

      expect(defaultProps.onClose).not.toHaveBeenCalled();
    });
  });

  describe('Object URL Management', () => {
    it('creates object URL for non-PDF content', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockImageBlob);
      const imageFile = { ...mockFile, fileName: 'image.png' };

      render(<FilePreviewOverlay {...defaultProps} file={imageFile} />);

      await waitFor(() => {
        expect(mockCreateObjectURL).toHaveBeenCalledWith(mockImageBlob);
      });
    });

    it('does not create object URL for PDF content', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockPdfBlob);
      const pdfFile = { ...mockFile, fileName: 'document.pdf' };

      render(<FilePreviewOverlay {...defaultProps} file={pdfFile} />);

      await waitFor(() => {
        expect(screen.getByTestId('pdf-viewer')).toBeInTheDocument();
      });

      expect(mockCreateObjectURL).not.toHaveBeenCalled();
    });

    it('revokes object URL on unmount', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockImageBlob);
      const imageFile = { ...mockFile, fileName: 'image.png' };

      const { unmount } = render(<FilePreviewOverlay {...defaultProps} file={imageFile} />);

      await waitFor(() => {
        expect(mockCreateObjectURL).toHaveBeenCalled();
      });

      unmount();

      expect(mockRevokeObjectURL).toHaveBeenCalledWith('mock-object-url');
    });
  });

  describe('Unsupported File Types', () => {
    it('shows fallback for unsupported file types', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(
        createMockBlob('binary-data', 'application/octet-stream')
      );
      const unknownFile = { ...mockFile, fileName: 'unknown.xyz' };

      render(<FilePreviewOverlay {...defaultProps} file={unknownFile} />);

      await waitFor(() => {
        expect(screen.getByText('No preview available')).toBeInTheDocument();
        expect(screen.getByText(/Preview not available for this file type/)).toBeInTheDocument();
        expect(screen.getByText(/You can download the file to view its contents/)).toBeInTheDocument();
      });
    });

    it('provides download link for unsupported files', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(
        createMockBlob('binary-data', 'application/octet-stream')
      );
      const unknownFile = { ...mockFile, fileName: 'unknown.xyz' };

      render(<FilePreviewOverlay {...defaultProps} file={unknownFile} />);

      await waitFor(() => {
        const downloadLink = screen.getByText('Download File');
        expect(downloadLink).toBeInTheDocument();
        expect(downloadLink).toHaveAttribute('href', 'mock-object-url');
        expect(downloadLink).toHaveAttribute('download', 'unknown.xyz');
      });
    });
  });

  describe('Markdown Detection', () => {
    it('detects .md files as markdown', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(
        createMockBlob('# Header', 'text/plain')
      );
      const mdFile = { ...mockFile, fileName: 'readme.md' };

      render(<FilePreviewOverlay {...defaultProps} file={mdFile} />);

      await waitFor(() => {
        expect(screen.getByTestId('markdown-viewer')).toBeInTheDocument();
      });
    });

    it('detects .markdown files as markdown', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(
        createMockBlob('# Header', 'text/plain')
      );
      const markdownFile = { ...mockFile, fileName: 'doc.markdown' };

      render(<FilePreviewOverlay {...defaultProps} file={markdownFile} />);

      await waitFor(() => {
        expect(screen.getByTestId('markdown-viewer')).toBeInTheDocument();
      });
    });

    it('shows processing markdown message', async () => {
      const mockBlob = {
        text: vi.fn().mockImplementation(() => new Promise(() => {})), // Never resolves
      } as any;
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockBlob);
      const mdFile = { ...mockFile, fileName: 'readme.md' };

      render(<FilePreviewOverlay {...defaultProps} file={mdFile} />);

      await waitFor(() => {
        expect(screen.getByText('Processing markdown...')).toBeInTheDocument();
      });
    });
  });

  describe('File Extension Handling', () => {
    const fileExtensions = [
      { ext: 'jpg', type: 'image/jpeg', viewer: 'image-viewer' },
      { ext: 'png', type: 'image/png', viewer: 'image-viewer' },
      { ext: 'gif', type: 'image/gif', viewer: 'image-viewer' },
      { ext: 'webp', type: 'image/webp', viewer: 'image-viewer' },
      { ext: 'svg', type: 'image/svg+xml', viewer: 'image-viewer' },
      { ext: 'mp3', type: 'audio/mpeg', viewer: 'audio-player' },
      { ext: 'wav', type: 'audio/wav', viewer: 'audio-player' },
      { ext: 'ogg', type: 'audio/ogg', viewer: 'audio-player' },
      { ext: 'mp4', type: 'video/mp4', viewer: 'video-player' },
      { ext: 'webm', type: 'video/webm', viewer: 'video-player' },
      { ext: 'py', type: 'text/x-python', viewer: 'text-viewer' },
      { ext: 'js', type: 'application/javascript', viewer: 'unsupported' },
      { ext: 'css', type: 'text/css', viewer: 'text-viewer' },
      { ext: 'xml', type: 'application/xml', viewer: 'unsupported' },
      { ext: 'csv', type: 'text/csv', viewer: 'text-viewer' },
      { ext: 'puml', type: 'text/plain', viewer: 'text-viewer' },
    ];

    fileExtensions.forEach(({ ext, type, viewer }) => {
      it(`handles .${ext} files correctly`, async () => {
        const mockBlob = viewer === 'text-viewer' || ext === 'json' 
          ? createMockBlob('content', type)
          : createMockBlob('content', type);
        (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockBlob);
        const testFile = { ...mockFile, fileName: `test.${ext}` };

        render(<FilePreviewOverlay {...defaultProps} file={testFile} />);

        await waitFor(() => {
          if (viewer === 'unsupported') {
            expect(screen.getByText('No preview available')).toBeInTheDocument();
          } else {
            expect(screen.getByTestId(viewer)).toBeInTheDocument();
          }
        });
      });
    });
  });

  describe('State Management', () => {
    it('resets state when file changes', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockTextBlob);

      const { rerender } = render(<FilePreviewOverlay {...defaultProps} file={mockFile} />);

      await waitFor(() => {
        expect(screen.getByTestId('text-viewer')).toBeInTheDocument();
      });

      // Change to a file that will error
      (notebookFilesApi.getNotebookFileContent as Mock).mockRejectedValue(
        new Error('New file error')
      );
      const newFile = { ...mockFile, id: 'file-2', fileName: 'error-file.txt' };

      rerender(<FilePreviewOverlay {...defaultProps} file={newFile} />);

      const dialog = await screen.findByRole('dialog');
      expect(within(dialog).getByText('New file error')).toBeInTheDocument();
      expect(screen.getAllByText('New file error').length).toBeGreaterThanOrEqual(2);
    });

    it('handles rapid file changes', async () => {
      let resolveFirst: (value: Blob) => void;
      let resolveSecond: (value: Blob) => void;

      const firstPromise = new Promise<Blob>((resolve) => {
        resolveFirst = resolve;
      });
      const secondPromise = new Promise<Blob>((resolve) => {
        resolveSecond = resolve;
      });

      (notebookFilesApi.getNotebookFileContent as Mock)
        .mockReturnValueOnce(firstPromise)
        .mockReturnValueOnce(secondPromise);

      const { rerender } = render(<FilePreviewOverlay {...defaultProps} file={mockFile} />);

      const secondFile = { ...mockFile, id: 'file-2', fileName: 'second.txt' };
      rerender(<FilePreviewOverlay {...defaultProps} file={secondFile} />);

      // Resolve second request first
      resolveSecond!(createMockBlob('second content', 'text/plain'));

      await waitFor(() => {
        expect(screen.getByText('second content')).toBeInTheDocument();
      });

      // Resolve first request - should not affect display
      resolveFirst!(createMockBlob('first content', 'text/plain'));

      // Should still show second content
      expect(screen.getByText('second content')).toBeInTheDocument();
      expect(screen.queryByText('first content')).not.toBeInTheDocument();
    });
  });

  describe('Embedded and fullscreen modes', () => {
    it('renders embedded layout without backdrop close behavior', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockTextBlob);

      render(<FilePreviewOverlay {...defaultProps} file={mockFile} isEmbedded />);

      await waitFor(() => {
        expect(screen.getByTestId('text-viewer')).toBeInTheDocument();
      });

      const overlay = screen.getByText('test-file.txt').closest('.h-full');
      fireEvent.click(overlay!);
      expect(defaultProps.onClose).not.toHaveBeenCalled();
      expect(screen.queryByLabelText('Close')).not.toBeInTheDocument();
    });

    it('toggles fullscreen from overlay controls', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockTextBlob);

      render(<FilePreviewOverlay {...defaultProps} file={mockFile} />);

      await waitFor(() => {
        expect(screen.getByTestId('text-viewer')).toBeInTheDocument();
      });

      fireEvent.click(screen.getByLabelText('Full screen'));
      expect(document.querySelector('.fixed.inset-0.z-50.bg-white')).toBeTruthy();

      fireEvent.click(screen.getByLabelText('Exit full screen'));
      expect(document.querySelector('.bg-black.bg-opacity-60')).toBeTruthy();
    });

    it('closes via close button and Escape key', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockTextBlob);

      render(<FilePreviewOverlay {...defaultProps} file={mockFile} />);

      await waitFor(() => {
        expect(screen.getByTestId('text-viewer')).toBeInTheDocument();
      });

      fireEvent.click(screen.getByLabelText('Close'));
      expect(defaultProps.onClose).toHaveBeenCalledTimes(1);

      fireEvent.keyDown(window, { key: 'Escape' });
      expect(defaultProps.onClose).toHaveBeenCalledTimes(2);
    });
  });

  describe('Markdown extraction tabs', () => {
    it('shows extraction tabs and switches to extracted text', async () => {
      const pdfFile = { ...mockFile, fileName: 'report.pdf' };
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockPdfBlob);
      (notebookFilesApi.getNotebookFileMarkdownShadow as Mock).mockResolvedValue({
        status: MarkdownExtractionStatus.Completed,
        contentHash: 'md-hash',
      });
      (notebookFilesApi.getNotebookFileMarkdownContent as Mock).mockResolvedValue({
        blob: createMockBlob('Extracted markdown body', 'text/markdown'),
      });

      render(<FilePreviewOverlay {...defaultProps} file={pdfFile} />);

      await waitFor(() => {
        expect(screen.getByText('Original Content')).toBeInTheDocument();
        expect(screen.getByText(/Extracted Text/)).toBeInTheDocument();
      });

      fireEvent.click(screen.getByText(/Extracted Text/));

      await waitFor(() => {
        expect(screen.getByText('Extracted markdown body')).toBeInTheDocument();
      });
    });

    it('shows failed extraction status on the extracted text tab label', async () => {
      const pdfFile = { ...mockFile, fileName: 'broken.pdf' };
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockPdfBlob);
      (notebookFilesApi.getNotebookFileMarkdownShadow as Mock).mockResolvedValue({
        status: MarkdownExtractionStatus.Failed,
        contentHash: 'md-hash',
      });

      render(<FilePreviewOverlay {...defaultProps} file={pdfFile} />);

      await waitFor(() => {
        expect(screen.getByText('Failed')).toBeInTheDocument();
        expect(screen.getByRole('button', { name: /Extracted Text/i })).toBeDisabled();
      });
    });

    it('shows markdown load error with retry on the extracted text tab', async () => {
      const pdfFile = { ...mockFile, fileName: 'report.pdf' };
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockPdfBlob);
      (notebookFilesApi.getNotebookFileMarkdownShadow as Mock).mockResolvedValue({
        status: MarkdownExtractionStatus.Completed,
        contentHash: 'md-hash',
      });
      (notebookFilesApi.getNotebookFileMarkdownContent as Mock).mockRejectedValue(
        new Error('extraction read failed')
      );

      render(<FilePreviewOverlay {...defaultProps} file={pdfFile} />);

      await waitFor(() => {
        expect(screen.getByText(/Extracted Text/)).toBeInTheDocument();
      });

      fireEvent.click(screen.getByText(/Extracted Text/));

      await waitFor(() => {
        expect(screen.getByText(/Failed to load markdown content/i)).toBeInTheDocument();
      });

      fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
      expect(notebookFilesApi.getNotebookFileMarkdownContent).toHaveBeenCalledTimes(2);
    });

    it('shows loading state while extracted markdown is being fetched', async () => {
      const pdfFile = { ...mockFile, fileName: 'report.pdf' };
      let resolveFetch: (value: { blob: Blob }) => void = () => {};
      const pending = new Promise<{ blob: Blob }>((resolve) => {
        resolveFetch = resolve;
      });
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockPdfBlob);
      (notebookFilesApi.getNotebookFileMarkdownShadow as Mock).mockResolvedValue({
        status: MarkdownExtractionStatus.Completed,
        contentHash: 'md-hash',
      });
      (notebookFilesApi.getNotebookFileMarkdownContent as Mock).mockReturnValue(pending);

      render(<FilePreviewOverlay {...defaultProps} file={pdfFile} />);

      await waitFor(() => {
        expect(screen.getByText(/Extracted Text/)).toBeInTheDocument();
      });

      fireEvent.click(screen.getByText(/Extracted Text/));
      expect(screen.getByText(/Loading extracted text/i)).toBeInTheDocument();

      await act(async () => {
        resolveFetch({ blob: createMockBlob('loaded markdown', 'text/markdown') });
      });

      await waitFor(() => {
        expect(screen.getByText('loaded markdown')).toBeInTheDocument();
      });
    });

    it('shows placeholder when extracted markdown content is empty', async () => {
      const pdfFile = { ...mockFile, fileName: 'report.pdf' };
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockPdfBlob);
      (notebookFilesApi.getNotebookFileMarkdownShadow as Mock).mockResolvedValue({
        status: MarkdownExtractionStatus.Completed,
        contentHash: 'md-hash',
      });
      (notebookFilesApi.getNotebookFileMarkdownContent as Mock).mockResolvedValue({
        blob: createMockBlob('', 'text/markdown'),
      });

      render(<FilePreviewOverlay {...defaultProps} file={pdfFile} />);

      await waitFor(() => {
        expect(screen.getByText(/Extracted Text/)).toBeInTheDocument();
      });

      fireEvent.click(screen.getByText(/Extracted Text/));

      await waitFor(() => {
        expect(screen.getByText('No markdown content available')).toBeInTheDocument();
      });
    });
  });

  describe('HTML preview navigation', () => {
    it('renders resolved HTML in iframe', async () => {
      const htmlFile = { ...mockFile, fileName: 'index.html', relativePath: 'site/index.html' };
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(
        createMockBlob('<html><body>Hello</body></html>', 'text/html')
      );

      render(<FilePreviewOverlay {...defaultProps} file={htmlFile} />);

      await waitFor(() => {
        expect(resolveHtmlResources).toHaveBeenCalled();
        expect(screen.getByTitle('HTML Preview')).toHaveAttribute(
          'srcDoc',
          '<html><body>Resolved HTML</body></html>'
        );
      });
    });

    it('resolves relative and parent-relative navigation targets', async () => {
      const htmlFile = { ...mockFile, fileName: 'page.html', relativePath: 'site/deep/page.html' };
      const onNavigate = vi.fn();
      const fileExists = vi.fn(
        (path: string) =>
          path === 'site/sibling.html' || path === 'site/deep/index.html' || path === 'site/index.html'
      );

      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(
        createMockBlob('<html><body>Hello</body></html>', 'text/html')
      );

      render(
        <FilePreviewOverlay
          {...defaultProps}
          file={htmlFile}
          onNavigate={onNavigate}
          fileExists={fileExists}
        />
      );

      await waitFor(() => {
        expect(screen.getByTitle('HTML Preview')).toBeInTheDocument();
      });

      window.dispatchEvent(
        new MessageEvent('message', {
          data: { type: 'html-preview-navigate', href: '../sibling.html' },
        })
      );

      await waitFor(() => {
        expect(onNavigate).toHaveBeenCalledWith('site/sibling.html');
      });

      window.dispatchEvent(
        new MessageEvent('message', {
          data: { type: 'html-preview-navigate', href: './' },
        })
      );

      await waitFor(() => {
        expect(onNavigate).toHaveBeenCalledWith('site/deep/index.html');
      });
    });

    it('does not navigate when the resolved target file is missing', async () => {
      const htmlFile = { ...mockFile, fileName: 'index.html', relativePath: 'site/index.html' };
      const onNavigate = vi.fn();
      const fileExists = vi.fn().mockReturnValue(false);
      const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});

      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(
        createMockBlob('<html><body>Hello</body></html>', 'text/html')
      );

      render(
        <FilePreviewOverlay
          {...defaultProps}
          file={htmlFile}
          onNavigate={onNavigate}
          fileExists={fileExists}
        />
      );

      await waitFor(() => {
        expect(screen.getByTitle('HTML Preview')).toBeInTheDocument();
      });

      window.dispatchEvent(
        new MessageEvent('message', {
          data: { type: 'html-preview-navigate', href: 'missing/page.html' },
        })
      );

      await waitFor(() => {
        expect(onNavigate).not.toHaveBeenCalled();
        expect(warnSpy).toHaveBeenCalled();
      });
      warnSpy.mockRestore();
    });

    it('navigates to linked file via postMessage', async () => {
      const htmlFile = { ...mockFile, fileName: 'index.html', relativePath: 'site/index.html' };
      const onNavigate = vi.fn();
      const fileExists = vi.fn().mockReturnValue(true);
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(
        createMockBlob('<html><body>Hello</body></html>', 'text/html')
      );

      render(
        <FilePreviewOverlay
          {...defaultProps}
          file={htmlFile}
          onNavigate={onNavigate}
          fileExists={fileExists}
        />
      );

      await waitFor(() => {
        expect(screen.getByTitle('HTML Preview')).toBeInTheDocument();
      });

      window.dispatchEvent(
        new MessageEvent('message', {
          data: { type: 'html-preview-navigate', href: '/other/page.html' },
        })
      );

      await waitFor(() => {
        expect(onNavigate).toHaveBeenCalledWith('site/other/page.html');
      });
    });
  });

  describe('DocumentServer Integration', () => {
    it('does not request DocumentServer capabilities for image previews', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockImageBlob);
      const imageFile = { ...mockFile, fileName: 'image.png' };

      render(<FilePreviewOverlay {...defaultProps} file={imageFile} />);

      await waitFor(() => {
        expect(screen.getByTestId('image-viewer')).toBeInTheDocument();
      });
      expect(getDocumentServerCapabilities).not.toHaveBeenCalled();
    });

    it('renders DocumentServer viewer for supported file types when enabled', async () => {
      (looksLikeDocumentServerFile as Mock).mockReturnValue(true);
      (getDocumentServerCapabilities as Mock).mockResolvedValue({
        enabled: true,
        publicUrl: 'http://localhost:8082',
        supportedExtensions: ['docx'],
        supportedContentTypes: [],
      });
      (isDocumentServerSupportedByExtension as Mock).mockReturnValue(true);
      const docxFile = { ...mockFile, fileName: 'proposal.docx', relativePath: 'proposal.docx' };

      render(<FilePreviewOverlay {...defaultProps} file={docxFile} />);

      await waitFor(() => {
        expect(screen.getByTestId('documentserver-editor')).toBeInTheDocument();
      });
      expect(screen.getByTestId('documentserver-editor')).toHaveAttribute('data-show-error-dialog-on-error', 'false');
    });

    it('downloads file content when download button is clicked', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockTextBlob);
      const clickSpy = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

      render(<FilePreviewOverlay {...defaultProps} file={mockFile} />);

      await waitFor(() => {
        expect(screen.getByTestId('text-viewer')).toBeInTheDocument();
      });

      fireEvent.click(screen.getByLabelText('Download'));
      expect(clickSpy).toHaveBeenCalled();
      clickSpy.mockRestore();
    });

    it('auto-switches to extracted text for non-previewable files', async () => {
      const docFile = { ...mockFile, fileName: 'slides.pptx', relativePath: 'slides.pptx' };
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(
        createMockBlob('binary', 'application/vnd.openxmlformats-officedocument.presentationml.presentation')
      );
      (notebookFilesApi.getNotebookFileMarkdownShadow as Mock).mockResolvedValue({
        status: MarkdownExtractionStatus.Completed,
        contentHash: 'hash-doc',
      });
      (notebookFilesApi.getNotebookFileMarkdownContent as Mock).mockResolvedValue({
        blob: createMockBlob('Extracted from doc', 'text/markdown'),
      });

      render(<FilePreviewOverlay {...defaultProps} file={docFile} />);

      await waitFor(() => {
        expect(screen.getByText('Extracted from doc')).toBeInTheDocument();
      });
    });

    it('shows configuration error when capabilities fetch fails', async () => {
      (looksLikeDocumentServerFile as Mock).mockReturnValue(true);
      (getDocumentServerCapabilities as Mock).mockRejectedValue(new Error('config missing'));
      const docxFile = { ...mockFile, fileName: 'proposal.docx', relativePath: 'proposal.docx' };

      render(<FilePreviewOverlay {...defaultProps} file={docxFile} />);

      await waitFor(() => {
        expect(screen.getByText('DocumentServer configuration error')).toBeInTheDocument();
      });
    });
  });

  describe('Markdown editing and extraction tab states', () => {
    it('opens markdown editor and saves updated content', async () => {
      const mdFile = { ...mockFile, fileName: 'notes.md', relativePath: 'notes.md' };
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(
        createMockBlob('# Hello', 'text/markdown')
      );
      (notebookFilesApi.getNotebookFileMarkdownContent as Mock).mockResolvedValue({
        blob: createMockBlob('# Hello', 'text/markdown'),
      });

      render(<FilePreviewOverlay {...defaultProps} file={mdFile} canEdit />);

      await waitFor(() => {
        expect(screen.getByTestId('markdown-viewer')).toBeInTheDocument();
      });

      fireEvent.click(screen.getByLabelText('Edit markdown'));
      expect(await screen.findByTestId('fullscreen-md-editor')).toBeInTheDocument();

      fireEvent.click(screen.getByText('Save markdown'));

      await waitFor(() => {
        expect(notebookFilesApi.uploadFiles).toHaveBeenCalled();
        expect(screen.queryByTestId('fullscreen-md-editor')).not.toBeInTheDocument();
      });
    });

    it('shows original content error dialog when text extraction fails', async () => {
      const failingBlob = createMockBlob('broken', 'text/plain');
      failingBlob.text = async () => {
        throw new Error('read failed');
      };
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(failingBlob);

      render(<FilePreviewOverlay {...defaultProps} file={mockFile} />);

      const dialog = await screen.findByRole('dialog');
      expect(within(dialog).getByText('Failed to read text content. Please try again.')).toBeInTheDocument();

      fireEvent.click(within(dialog).getByText('OK'));
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });

    it('shows processing badge and switches back to original tab', async () => {
      const pdfFile = { ...mockFile, fileName: 'report.pdf' };
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockPdfBlob);
      (notebookFilesApi.getNotebookFileMarkdownShadow as Mock).mockResolvedValue({
        status: MarkdownExtractionStatus.Processing,
        contentHash: 'md-hash',
      });

      render(<FilePreviewOverlay {...defaultProps} file={pdfFile} />);

      await waitFor(() => {
        expect(screen.getByText('Processing')).toBeInTheDocument();
      });

      fireEvent.click(screen.getByText('Original Content'));
      await waitFor(() => {
        expect(screen.getByTestId('pdf-viewer')).toBeInTheDocument();
      });
    });

    it('does not close overlay on Escape while markdown editor is open', async () => {
      const mdFile = { ...mockFile, fileName: 'notes.md', relativePath: 'notes.md' };
      const onClose = vi.fn();
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(
        createMockBlob('# Hello', 'text/markdown')
      );
      (notebookFilesApi.getNotebookFileMarkdownContent as Mock).mockResolvedValue({
        blob: createMockBlob('# Hello', 'text/markdown'),
      });

      render(<FilePreviewOverlay {...defaultProps} file={mdFile} canEdit onClose={onClose} />);

      await waitFor(() => {
        expect(screen.getByTestId('markdown-viewer')).toBeInTheDocument();
      });

      fireEvent.click(screen.getByLabelText('Edit markdown'));
      await screen.findByTestId('fullscreen-md-editor');
      fireEvent.keyDown(window, { key: 'Escape' });

      expect(onClose).not.toHaveBeenCalled();
      expect(screen.getByTestId('fullscreen-md-editor')).toBeInTheDocument();
    });

    it('cancels markdown editor without saving', async () => {
      const mdFile = { ...mockFile, fileName: 'notes.md', relativePath: 'notes.md' };
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(
        createMockBlob('# Hello', 'text/markdown')
      );
      (notebookFilesApi.getNotebookFileMarkdownContent as Mock).mockResolvedValue({
        blob: createMockBlob('# Hello', 'text/markdown'),
      });

      render(<FilePreviewOverlay {...defaultProps} file={mdFile} canEdit />);

      await waitFor(() => {
        expect(screen.getByTestId('markdown-viewer')).toBeInTheDocument();
      });

      fireEvent.click(screen.getByLabelText('Edit markdown'));
      fireEvent.click(await screen.findByText('Cancel markdown'));

      expect(screen.queryByTestId('fullscreen-md-editor')).not.toBeInTheDocument();
      expect(notebookFilesApi.uploadFiles).not.toHaveBeenCalled();
    });

    it('opens preview in a new window when embedded in a host surface', async () => {
      const openSpy = vi.spyOn(window, 'open').mockImplementation(() => null);
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockTextBlob);

      render(<FilePreviewOverlay {...defaultProps} file={mockFile} isEmbedded />);

      await waitFor(() => {
        expect(screen.getByTestId('text-viewer')).toBeInTheDocument();
      });

      fireEvent.click(screen.getByLabelText('Open in new window'));
      expect(openSpy).toHaveBeenCalled();
      openSpy.mockRestore();
    });
  });
}); 
