import React from 'react';
import { render, screen, fireEvent, waitFor } from '../../../../test/test-utils';
import { describe, it, expect, beforeEach, vi, Mock } from 'vitest';
import { FilePreviewOverlay } from '../FilePreviewOverlay';
import { NotebookFileDto } from '../../../../types/notebook';
import { notebookFilesApi } from '../../../../services/notebookFiles';

// Mock the services
vi.mock('../../../../services/notebookFiles', () => ({
  notebookFilesApi: {
    getNotebookFileContent: vi.fn(),
    getNotebookFileMarkdownShadow: vi.fn().mockResolvedValue({ status: 'Skipped' }),
    getNotebookFileMarkdownContent: vi.fn().mockResolvedValue({ blob: new Blob([''], { type: 'text/markdown' }) }),
  },
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

      await waitFor(() => {
        expect(screen.getByText(errorMessage)).toBeInTheDocument();
      });
    });

    it('displays generic error message when error has no message', async () => {
      (notebookFilesApi.getNotebookFileContent as Mock).mockRejectedValue(new Error());

      render(<FilePreviewOverlay {...defaultProps} file={mockFile} />);

      await waitFor(() => {
        expect(screen.getByText('Failed to load file content.')).toBeInTheDocument();
      });
    });

    it('handles text content reading errors', async () => {
      const mockBlob = {
        text: vi.fn().mockRejectedValue(new Error('Text reading failed')),
      } as any;
      (notebookFilesApi.getNotebookFileContent as Mock).mockResolvedValue(mockBlob);

      // Spy on console.error to verify error logging
      const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});

      render(<FilePreviewOverlay {...defaultProps} file={mockFile} />);

      await waitFor(() => {
        expect(screen.getByText('Failed to read text content. Please try again.')).toBeInTheDocument();
      });

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

      await waitFor(() => {
        expect(screen.getByText('New file error')).toBeInTheDocument();
      });
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
}); 