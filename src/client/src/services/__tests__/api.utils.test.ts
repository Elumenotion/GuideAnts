import { describe, it, expect, vi, beforeEach } from 'vitest';
import { api } from '../api';

const mockFetch = vi.fn();

// @ts-ignore
global.fetch = mockFetch;

describe('api.utils.getAuthenticatedUrl (table-driven)', () => {
  beforeEach(() => {
    mockFetch.mockReset();
  });

  const filenameCases: Array<{
    name: string;
    url: string;
    contentDisposition: string | null;
    expectedFileName: string;
  }> = [
    {
      name: 'from content-disposition',
      url: 'http://localhost/api/files/1/content',
      contentDisposition: 'attachment; filename="report.pdf"',
      expectedFileName: 'report.pdf',
    },
    {
      name: 'generic filename falls back to path query',
      url: 'http://localhost/api/files/content?path=%2Fdocs%2Freadme.md',
      contentDisposition: 'attachment; filename=download',
      expectedFileName: 'readme.md',
    },
    {
      name: 'missing disposition uses pathname segment',
      url: 'http://localhost/api/projects/p1/files/f99/content',
      contentDisposition: null,
      expectedFileName: 'content',
    },
    {
      name: 'ultimate fallback is download',
      url: 'http://localhost/api/download',
      contentDisposition: 'attachment; filename=file',
      expectedFileName: 'download',
    },
  ];

  it.each(filenameCases)('$name', async ({ url, contentDisposition, expectedFileName }) => {
    const blob = new Blob(['data'], { type: 'application/octet-stream' });
    mockFetch.mockResolvedValue({
      ok: true,
      status: 200,
      blob: vi.fn().mockResolvedValue(blob),
      headers: {
        get: vi.fn((name: string) => {
          if (name === 'content-disposition') return contentDisposition;
          if (name === 'content-type') return 'application/pdf';
          return null;
        }),
      },
    });

    const createObjectURL = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:test');
    const result = await api.utils.getAuthenticatedUrl(url);

    expect(result.fileName).toBe(expectedFileName);
    expect(result.contentType).toBe('application/pdf');
    expect(result.objectUrl).toBe('blob:test');
    createObjectURL.mockRestore();
  });
});
