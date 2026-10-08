import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  BLANK_OFFICE_DOCUMENTS,
  createBlankOfficeFile,
  getBlankOfficeDocument,
  getUniqueFileName,
} from '../blankOfficeDocuments';

const templateResponse = (init: { ok?: boolean; status?: number; contentType?: string } = {}) => ({
  ok: init.ok ?? true,
  status: init.status ?? 200,
  headers: new Headers({ 'content-type': init.contentType ?? 'application/octet-stream' }),
  blob: vi.fn().mockResolvedValue(new Blob(['PK-template-bytes'])),
});

describe('getUniqueFileName', () => {
  it('keeps the proposed name when it is free', () => {
    expect(getUniqueFileName('New Document.docx', ['notes.md'])).toBe('New Document.docx');
  });

  it('appends (2) on a collision', () => {
    expect(getUniqueFileName('New Document.docx', ['New Document.docx'])).toBe('New Document (2).docx');
  });

  it('skips numbered names that are already taken', () => {
    expect(
      getUniqueFileName('New Document.docx', ['New Document.docx', 'New Document (2).docx'])
    ).toBe('New Document (3).docx');
  });

  it('matches existing names case-insensitively', () => {
    expect(getUniqueFileName('New Document.docx', ['new document.DOCX'])).toBe('New Document (2).docx');
  });

  it('handles names without an extension', () => {
    expect(getUniqueFileName('README', ['readme'])).toBe('README (2)');
  });
});

describe('BLANK_OFFICE_DOCUMENTS', () => {
  it('lists Word, Excel and PowerPoint in menu order', () => {
    expect(BLANK_OFFICE_DOCUMENTS.map((d) => d.menuLabel)).toEqual([
      'New Word Document',
      'New Excel Spreadsheet',
      'New PowerPoint Presentation',
    ]);
  });

  it('gives every definition a template URL', () => {
    for (const definition of BLANK_OFFICE_DOCUMENTS) {
      expect(definition.templateUrl).toEqual(expect.any(String));
      expect(definition.templateUrl.length).toBeGreaterThan(0);
    }
  });

  it('looks definitions up by kind', () => {
    expect(getBlankOfficeDocument('xlsx').defaultFileName).toBe('New Spreadsheet.xlsx');
  });
});

describe('createBlankOfficeFile', () => {
  const fetchMock = vi.fn();

  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal('fetch', fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it.each([
    ['docx', 'New Document.docx', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'],
    ['xlsx', 'New Spreadsheet.xlsx', 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'],
    ['pptx', 'New Presentation.pptx', 'application/vnd.openxmlformats-officedocument.presentationml.presentation'],
  ] as const)('builds a %s File named %s with type %s', async (kind, fileName, contentType) => {
    fetchMock.mockResolvedValue(templateResponse());

    const file = await createBlankOfficeFile(kind, []);

    expect(fetchMock).toHaveBeenCalledWith(getBlankOfficeDocument(kind).templateUrl);
    expect(file.name).toBe(fileName);
    expect(file.type).toBe(contentType);
    expect(file.size).toBeGreaterThan(0);
  });

  it("picks a name that doesn't collide with the folder's files", async () => {
    fetchMock.mockResolvedValue(templateResponse());

    const file = await createBlankOfficeFile('xlsx', ['New Spreadsheet.xlsx']);

    expect(file.name).toBe('New Spreadsheet (2).xlsx');
  });

  it('throws when the template request fails', async () => {
    fetchMock.mockResolvedValue(templateResponse({ ok: false, status: 404 }));

    await expect(createBlankOfficeFile('docx', [])).rejects.toThrow('HTTP 404');
  });

  it('refuses an HTML fallback page served in place of the template', async () => {
    fetchMock.mockResolvedValue(templateResponse({ contentType: 'text/html; charset=utf-8' }));

    await expect(createBlankOfficeFile('pptx', [])).rejects.toThrow(/HTML/);
  });
});
