import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '../../../../test/test-utils';
import { FileContents } from '../FileContents';
import { api } from '../../../../services/api';
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
  isDocumentServerSupportedByContentType: vi.fn(),
  isDocumentServerSupportedByExtension: vi.fn(),
  looksLikeDocumentServerFile: vi.fn(() => false),
}));

vi.mock('../../../common/DocumentServerEditor', () => ({
  default: () => <div data-testid="documentserver-editor">DocumentServer</div>,
}));

describe('FileContents DocumentServer gating', () => {
  const getContentFileContentMock = vi.mocked(api.projects.getContentFileContent);
  const getDocumentServerCapabilitiesMock = vi.mocked(getDocumentServerCapabilities);
  const isDocumentServerSupportedByContentTypeMock = vi.mocked(isDocumentServerSupportedByContentType);
  const isDocumentServerSupportedByExtensionMock = vi.mocked(isDocumentServerSupportedByExtension);
  const looksLikeDocumentServerFileMock = vi.mocked(looksLikeDocumentServerFile);

  beforeEach(() => {
    vi.clearAllMocks();
    looksLikeDocumentServerFileMock.mockReturnValue(false);
    isDocumentServerSupportedByExtensionMock.mockReturnValue(false);
  });

  it('does not request DocumentServer capabilities for non-office content', async () => {
    isDocumentServerSupportedByContentTypeMock.mockReturnValue(false);
    getContentFileContentMock.mockResolvedValue({
      blob: new Blob([new Uint8Array([0x00, 0xff, 0x7f, 0x10])], { type: 'application/octet-stream' }),
      contentType: 'application/octet-stream',
      fileName: 'archive.bin',
    });

    render(
      <FileContents
        projectId="project-1"
        fileId="file-1"
        contentType="application/octet-stream"
      />
    );

    expect(await screen.findByText(/Preview not available for this file type/i)).toBeInTheDocument();
    expect(getDocumentServerCapabilitiesMock).not.toHaveBeenCalled();
  });

  it('renders DocumentServer editor when enabled and supported', async () => {
    looksLikeDocumentServerFileMock.mockReturnValue(true);
    getDocumentServerCapabilitiesMock.mockResolvedValue({
      enabled: true,
      publicUrl: 'http://localhost:8082',
      supportedExtensions: ['docx'],
      supportedContentTypes: ['application/vnd.openxmlformats-officedocument.wordprocessingml.document'],
    });
    isDocumentServerSupportedByContentTypeMock.mockReturnValue(true);
    getContentFileContentMock.mockResolvedValue({
      blob: new Blob(['ignored'], { type: 'application/octet-stream' }),
      contentType: 'application/octet-stream',
      fileName: 'ignored.bin',
    });

    render(
      <FileContents
        projectId="project-1"
        fileId="file-1"
        contentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document"
      />
    );

    await waitFor(() => {
      expect(screen.getByTestId('documentserver-editor')).toBeInTheDocument();
    });
  });

  it('keeps DocumentServer routing stable after content is cleared for the editor', async () => {
    looksLikeDocumentServerFileMock.mockReturnValue(true);
    getDocumentServerCapabilitiesMock.mockResolvedValue({
      enabled: true,
      publicUrl: 'http://localhost:8082',
      supportedExtensions: ['csv'],
      supportedContentTypes: [],
    });
    isDocumentServerSupportedByContentTypeMock.mockReturnValue(false);
    isDocumentServerSupportedByExtensionMock.mockImplementation((_fileName, capabilities) => Boolean(capabilities));
    getContentFileContentMock.mockResolvedValue({
      blob: new Blob(['one,two'], { type: 'text/csv' }),
      contentType: 'text/csv',
      fileName: 'AzureUsage (2).csv',
    });

    render(
      <FileContents
        projectId="project-1"
        fileId="file-1"
        fileName="AzureUsage (2).csv"
        contentType="text/csv"
      />
    );

    await waitFor(() => {
      expect(screen.getByTestId('documentserver-editor')).toBeInTheDocument();
    });

    await new Promise((resolve) => setTimeout(resolve, 25));

    expect(getContentFileContentMock).toHaveBeenCalledTimes(1);
    expect(looksLikeDocumentServerFileMock).toHaveBeenLastCalledWith('AzureUsage (2).csv', 'text/csv');
  });
});
