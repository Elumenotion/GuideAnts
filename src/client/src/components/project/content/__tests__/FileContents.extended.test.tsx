import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '../../../../test/test-utils';
import { FileContents } from '../FileContents';
import { resolveHtmlResources, cleanupBlobUrls } from '../../../../utils/htmlResourceResolver';
import {
  getDocumentServerCapabilities,
  isDocumentServerSupportedByContentType,
  isDocumentServerSupportedByExtension,
  looksLikeDocumentServerFile,
} from '../../../../services/documentServer';

vi.mock('../../../../services/api', () => ({
  api: {
    projects: {
      getContentFileContent: vi.fn(),
    },
  },
}));

vi.mock('../../../../services/documentServer', () => ({
  getDocumentServerCapabilities: vi.fn(),
  isDocumentServerSupportedByContentType: vi.fn(() => false),
  isDocumentServerSupportedByExtension: vi.fn(() => false),
  looksLikeDocumentServerFile: vi.fn(() => false),
}));

vi.mock('../../../../utils/htmlResourceResolver', () => ({
  resolveHtmlResources: vi.fn(),
  cleanupBlobUrls: vi.fn(),
}));

vi.mock('../../../common/DocumentServerEditor', () => ({
  default: () => <div data-testid="documentserver-editor">DocumentServer</div>,
}));

import { api } from '../../../../services/api';

const getContentFileContent = vi.mocked(api.projects.getContentFileContent);
const resolveHtmlResourcesMock = vi.mocked(resolveHtmlResources);
const cleanupBlobUrlsMock = vi.mocked(cleanupBlobUrls);
const getDocumentServerCapabilitiesMock = vi.mocked(getDocumentServerCapabilities);
const looksLikeDocumentServerFileMock = vi.mocked(looksLikeDocumentServerFile);
const isDocumentServerSupportedByContentTypeMock = vi.mocked(isDocumentServerSupportedByContentType);
const isDocumentServerSupportedByExtensionMock = vi.mocked(isDocumentServerSupportedByExtension);

global.URL.createObjectURL = vi.fn(() => 'blob:http://localhost/mock-url');
global.URL.revokeObjectURL = vi.fn();

describe('FileContents extended branches', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    looksLikeDocumentServerFileMock.mockReturnValue(false);
    isDocumentServerSupportedByContentTypeMock.mockReturnValue(false);
    isDocumentServerSupportedByExtensionMock.mockReturnValue(false);
  });

  it('renders resolved HTML in an iframe', async () => {
    const html = '<html><body><h1>Hello</h1></body></html>';
    const mockBlob = {
      text: async () => html,
      type: 'text/html',
      size: html.length,
    };
    getContentFileContent.mockResolvedValueOnce({
      blob: mockBlob as unknown as Blob,
      contentType: 'text/html',
      fileName: 'pages/index.html',
    });
    resolveHtmlResourcesMock.mockResolvedValueOnce({
      html: '<html><body><h1>Resolved</h1></body></html>',
      blobUrls: new Set(['blob:resolved']),
    });

    render(<FileContents projectId="p1" fileId="f1" contentType="text/html" />);

    await waitFor(() => {
      const iframe = screen.getByTitle('HTML Preview') as HTMLIFrameElement;
      expect(iframe.srcdoc).toContain('Resolved');
    });
    expect(resolveHtmlResourcesMock).toHaveBeenCalledWith(
      expect.objectContaining({
        html,
        projectId: 'p1',
        basePath: 'pages',
      }),
    );
  });

  it('falls back to original HTML when resource resolution fails', async () => {
    const html = '<html><body>Fallback</body></html>';
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const mockBlob = {
      text: async () => html,
      type: 'text/html',
      size: html.length,
    };
    getContentFileContent.mockResolvedValueOnce({
      blob: mockBlob as unknown as Blob,
      contentType: 'text/html',
      fileName: 'index.html',
    });
    resolveHtmlResourcesMock.mockRejectedValueOnce(new Error('resolve failed'));

    render(<FileContents projectId="p1" fileId="f1" contentType="text/html" />);

    await waitFor(() => {
      const iframe = screen.getByTitle('HTML Preview') as HTMLIFrameElement;
      expect(iframe.srcdoc).toBe(html);
    });
    consoleSpy.mockRestore();
  });

  it('renders audio and video previews', async () => {
    const audioBlob = new Blob(['audio'], { type: 'audio/mpeg' });
    getContentFileContent.mockResolvedValueOnce({
      blob: audioBlob,
      contentType: 'audio/mpeg',
      fileName: 'clip.mp3',
    });

    const { container: audioContainer, unmount } = render(
      <FileContents projectId="p1" fileId="f1" contentType="audio/mpeg" />,
    );
    await waitFor(() => {
      expect(audioContainer.querySelector('audio')).toBeInTheDocument();
    });
    unmount();

    const videoBlob = new Blob(['video'], { type: 'video/mp4' });
    getContentFileContent.mockResolvedValueOnce({
      blob: videoBlob,
      contentType: 'video/mp4',
      fileName: 'clip.mp4',
    });

    const { container: videoContainer } = render(
      <FileContents projectId="p1" fileId="f2" contentType="video/mp4" />,
    );
    await waitFor(() => {
      expect(videoContainer.querySelector('video')).toBeInTheDocument();
    });
  });

  it('shows markdown edit action when callback is provided', async () => {
    const onEditMarkdown = vi.fn();
    const mockBlob = {
      text: async () => '# Title',
      type: 'text/markdown',
      size: 7,
    };
    getContentFileContent.mockResolvedValueOnce({
      blob: mockBlob as unknown as Blob,
      contentType: 'text/markdown',
      fileName: 'readme.md',
    });

    render(
      <FileContents
        projectId="p1"
        fileId="f1"
        contentType="text/markdown"
        onEditMarkdown={onEditMarkdown}
      />,
    );

    await screen.findByText('Title');
    const fullscreenButton = screen.getByLabelText('Full screen');
    fullscreenButton.click();

    const editButton = await screen.findByLabelText('Edit markdown');
    expect(editButton).toBeInTheDocument();
    editButton.click();
    expect(onEditMarkdown).toHaveBeenCalledTimes(1);
  });

  it('shows DocumentServer configuration error when capabilities fetch fails', async () => {
    looksLikeDocumentServerFileMock.mockReturnValue(true);
    isDocumentServerSupportedByContentTypeMock.mockReturnValue(false);
    isDocumentServerSupportedByExtensionMock.mockReturnValue(false);
    getDocumentServerCapabilitiesMock.mockRejectedValueOnce(new Error('config down'));

    render(
      <FileContents
        projectId="p1"
        fileId="f1"
        fileName="report.docx"
        contentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document"
      />,
    );

    expect(await screen.findByText('DocumentServer configuration error')).toBeInTheDocument();
    expect(screen.getByText(/capabilities request failed/i)).toBeInTheDocument();
  });

  it('cleans up html blob urls on unmount', async () => {
    const html = '<html><body>Cleanup</body></html>';
    const mockBlob = {
      text: async () => html,
      type: 'text/html',
      size: html.length,
    };
    getContentFileContent.mockResolvedValueOnce({
      blob: mockBlob as unknown as Blob,
      contentType: 'text/html',
      fileName: 'index.html',
    });
    resolveHtmlResourcesMock.mockResolvedValueOnce({
      html,
      blobUrls: new Set(['blob:cleanup']),
    });

    const { unmount } = render(<FileContents projectId="p1" fileId="f1" contentType="text/html" />);
    await screen.findByTitle('HTML Preview');
    unmount();
    expect(cleanupBlobUrlsMock).toHaveBeenCalled();
  });
});
