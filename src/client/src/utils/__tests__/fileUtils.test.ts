import { describe, expect, it } from 'vitest';
import {
  formatFileSize,
  getContentTypeFromFileName,
  getFileExtension,
  getSupportedFileTypesDisplay,
  isFileIndexable,
} from '../fileUtils';

describe('fileUtils', () => {
  it('detects indexable file extensions', () => {
    expect(isFileIndexable('report.pdf')).toBe(true);
    expect(isFileIndexable('notes.md')).toBe(true);
    expect(isFileIndexable('photo.png')).toBe(false);
    expect(isFileIndexable('')).toBe(false);
  });

  it('returns file extensions with a leading dot', () => {
    expect(getFileExtension('archive.tar.gz')).toBe('.gz');
    expect(getFileExtension('README')).toBe('');
    expect(getFileExtension('')).toBe('');
  });

  it('formats supported file types for display', () => {
    expect(getSupportedFileTypesDisplay()).toContain('.pdf');
    expect(getSupportedFileTypesDisplay()).toContain('.docx');
  });

  it('maps common file names to content types', () => {
    expect(getContentTypeFromFileName('photo.jpg')).toBe('image/jpeg');
    expect(getContentTypeFromFileName('sheet.xlsx')).toBe(
      'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
    );
    expect(getContentTypeFromFileName('script.ts')).toBe('application/typescript');
    expect(getContentTypeFromFileName('unknown.bin')).toBe('application/octet-stream');
  });

  it('formats byte sizes for humans', () => {
    expect(formatFileSize(0)).toBe('0 B');
    expect(formatFileSize(1536)).toBe('1.5 KB');
    expect(formatFileSize(1048576)).toBe('1 MB');
  });
});
