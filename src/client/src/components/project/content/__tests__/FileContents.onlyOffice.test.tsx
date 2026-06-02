import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '../../../../test/test-utils';
import { FileContents } from '../FileContents';
import { api } from '../../../../services/api';
import { getOnlyOfficeCapabilities, isOnlyOfficeSupportedByContentType } from '../../../../services/onlyOffice';

vi.mock('../../../../services/api', () => ({
  api: {
    projects: {
      getContentFileContent: vi.fn(),
    },
  },
}));

vi.mock('../../../../services/onlyOffice', () => ({
  getOnlyOfficeCapabilities: vi.fn(),
  isOnlyOfficeSupportedByContentType: vi.fn(),
}));

vi.mock('../../../common/OnlyOfficeEditor', () => ({
  default: () => <div data-testid="onlyoffice-editor">ONLYOFFICE</div>,
}));

describe('FileContents ONLYOFFICE gating', () => {
  const getContentFileContentMock = vi.mocked(api.projects.getContentFileContent);
  const getOnlyOfficeCapabilitiesMock = vi.mocked(getOnlyOfficeCapabilities);
  const isOnlyOfficeSupportedByContentTypeMock = vi.mocked(isOnlyOfficeSupportedByContentType);

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('keeps unsupported preview message when ONLYOFFICE is disabled', async () => {
    getOnlyOfficeCapabilitiesMock.mockResolvedValue({
      enabled: false,
      publicUrl: '',
      supportedExtensions: [],
      supportedContentTypes: [],
    });
    isOnlyOfficeSupportedByContentTypeMock.mockReturnValue(false);
    getContentFileContentMock.mockResolvedValue({
      blob: new Blob(['binary'], { type: 'application/octet-stream' }),
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
  });

  it('renders ONLYOFFICE editor when enabled and supported', async () => {
    getOnlyOfficeCapabilitiesMock.mockResolvedValue({
      enabled: true,
      publicUrl: 'http://localhost:8082',
      supportedExtensions: ['docx'],
      supportedContentTypes: ['application/vnd.openxmlformats-officedocument.wordprocessingml.document'],
    });
    isOnlyOfficeSupportedByContentTypeMock.mockReturnValue(true);
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
      expect(screen.getByTestId('onlyoffice-editor')).toBeInTheDocument();
    });
  });
});
