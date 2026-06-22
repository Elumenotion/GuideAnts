import { beforeEach, describe, expect, it, vi } from 'vitest';

const mockBroadcastAuthExpired = vi.fn();

vi.mock('../authEvents', () => ({
  broadcastAuthExpired: (...args: unknown[]) => mockBroadcastAuthExpired(...args),
}));

vi.mock('../authService', () => ({
  withAuthFetchInit: (init: RequestInit) => ({ ...init, credentials: 'include' }),
  withAuthHeaders: (headers?: HeadersInit) => new Headers(headers),
}));

import {
  createDocumentServerEditorConfig,
  getDocumentServerCapabilities,
  isDocumentServerSupportedByContentType,
  isDocumentServerSupportedByExtension,
  looksLikeDocumentServerFile,
  type DocumentServerCapabilities,
} from '../documentServer';

const mockFetch = vi.fn();

const enabledCapabilities: DocumentServerCapabilities = {
  enabled: true,
  publicUrl: 'http://localhost:8082',
  supportedExtensions: ['docx', 'pdf'],
  supportedContentTypes: [
    'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    'application/pdf',
  ],
};

const htmlCapabilities: DocumentServerCapabilities = {
  ...enabledCapabilities,
  supportedExtensions: ['html', 'htm', 'docx'],
  supportedContentTypes: ['text/html', 'application/xhtml+xml', ...enabledCapabilities.supportedContentTypes],
};

describe('documentServer preview exclusions', () => {
  it('does not route PDF by extension to DocumentServer', () => {
    expect(isDocumentServerSupportedByExtension('sample.pdf', enabledCapabilities)).toBe(false);
    expect(isDocumentServerSupportedByExtension('sample.docx', enabledCapabilities)).toBe(true);
  });

  it('does not route PDF by content type to DocumentServer', () => {
    expect(isDocumentServerSupportedByContentType('application/pdf', enabledCapabilities)).toBe(false);
    expect(
      isDocumentServerSupportedByContentType(
        'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
        enabledCapabilities
      )
    ).toBe(true);
  });

  it('does not classify PDF as an DocumentServer candidate', () => {
    expect(looksLikeDocumentServerFile('sample.pdf', 'application/pdf')).toBe(false);
    expect(
      looksLikeDocumentServerFile(
        'sample.docx',
        'application/vnd.openxmlformats-officedocument.wordprocessingml.document'
      )
    ).toBe(true);
  });

  it('does not route HTML by extension to DocumentServer', () => {
    expect(isDocumentServerSupportedByExtension('index.html', htmlCapabilities)).toBe(false);
    expect(isDocumentServerSupportedByExtension('page.htm', htmlCapabilities)).toBe(false);
    expect(isDocumentServerSupportedByExtension('sample.docx', htmlCapabilities)).toBe(true);
  });

  it('does not route HTML by content type to DocumentServer', () => {
    expect(isDocumentServerSupportedByContentType('text/html', htmlCapabilities)).toBe(false);
    expect(isDocumentServerSupportedByContentType('application/xhtml+xml', htmlCapabilities)).toBe(false);
    expect(
      isDocumentServerSupportedByContentType(
        'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
        htmlCapabilities
      )
    ).toBe(true);
  });

  it('does not classify HTML as a DocumentServer candidate', () => {
    expect(looksLikeDocumentServerFile('index.html', 'text/html')).toBe(false);
    expect(looksLikeDocumentServerFile('page.htm', 'text/html')).toBe(false);
  });

  it('returns false when capabilities are disabled or missing', () => {
    expect(isDocumentServerSupportedByExtension('sample.docx', { ...enabledCapabilities, enabled: false })).toBe(false);
    expect(isDocumentServerSupportedByExtension('sample.docx', null)).toBe(false);
    expect(isDocumentServerSupportedByExtension('no-extension', enabledCapabilities)).toBe(false);
  });

  it('classifies office files by extension and content type markers', () => {
    expect(looksLikeDocumentServerFile('report.xlsx')).toBe(true);
    expect(
      looksLikeDocumentServerFile(
        'unknown.bin',
        'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'
      )
    ).toBe(true);
    expect(looksLikeDocumentServerFile('unknown.bin')).toBe(false);
  });
});

describe('documentServer API', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // @ts-expect-error test override
    global.fetch = mockFetch;
  });

  it('loads capabilities and reuses cache on subsequent calls', async () => {
    const payload: DocumentServerCapabilities = {
      enabled: true,
      publicUrl: 'http://localhost:8082',
      supportedExtensions: ['docx'],
      supportedContentTypes: ['application/vnd.openxmlformats-officedocument.wordprocessingml.document'],
    };
    mockFetch.mockResolvedValue({
      ok: true,
      status: 200,
      json: vi.fn().mockResolvedValue(payload),
    });

    await expect(getDocumentServerCapabilities(true)).resolves.toEqual(payload);
    await expect(getDocumentServerCapabilities()).resolves.toEqual(payload);
    expect(mockFetch).toHaveBeenCalledTimes(1);
  });

  it('throws formatted error when capabilities request fails', async () => {
    mockFetch.mockResolvedValue({
      ok: false,
      status: 503,
      text: vi.fn().mockResolvedValue(JSON.stringify({ message: 'Document server offline' })),
    });

    await expect(getDocumentServerCapabilities(true)).rejects.toThrow(
      'Document server offline (HTTP 503)'
    );
  });

  it('creates editor config on success', async () => {
    const response = {
      documentServerUrl: 'http://localhost:8082/editor',
      config: { documentType: 'word' },
    };
    mockFetch.mockResolvedValue({
      ok: true,
      status: 200,
      json: vi.fn().mockResolvedValue(response),
    });

    const request = {
      scope: 'notebook' as const,
      projectId: 'proj-1',
      notebookId: 'nb-1',
      fileId: 'file-1',
      canEdit: true,
    };

    await expect(createDocumentServerEditorConfig(request)).resolves.toEqual(response);
    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining('/documentserver/editor-config'),
      expect.objectContaining({
        method: 'POST',
        credentials: 'include',
        body: JSON.stringify(request),
      })
    );
  });

  it('broadcasts auth expired on 401 responses', async () => {
    mockFetch.mockResolvedValue({
      ok: false,
      status: 401,
      text: vi.fn().mockResolvedValue(''),
    });

    await expect(
      createDocumentServerEditorConfig({
        scope: 'project',
        projectId: 'proj-1',
        fileId: 'file-1',
        canEdit: false,
      })
    ).rejects.toThrow('HTTP 401');
    expect(mockBroadcastAuthExpired).toHaveBeenCalledWith('Authentication expired.');
  });

  it('throws timeout error when fetch aborts', async () => {
    const abortError = new DOMException('Aborted', 'AbortError');
    mockFetch.mockRejectedValue(abortError);

    await expect(getDocumentServerCapabilities(true)).rejects.toThrow(
      'DocumentServer request timed out after 10000ms.'
    );
  });
});
