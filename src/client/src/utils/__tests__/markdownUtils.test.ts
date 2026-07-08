import { describe, expect, it } from 'vitest';
import {
  getSupportedFileTypesDescription,
  isMarkdownExtractionSupported,
} from '../markdownUtils';

describe('markdownUtils', () => {
  it('accepts supported document and media files', () => {
    expect(isMarkdownExtractionSupported('report.pdf', 'application/pdf')).toBe(true);
    expect(isMarkdownExtractionSupported('notes.docx', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document')).toBe(true);
    expect(isMarkdownExtractionSupported('clip.mp4', 'video/mp4')).toBe(true);
    expect(isMarkdownExtractionSupported('voice.mp3', 'audio/mpeg')).toBe(true);
  });

  it('rejects unsupported or mismatched file types', () => {
    expect(isMarkdownExtractionSupported('', 'application/pdf')).toBe(false);
    expect(isMarkdownExtractionSupported('README', 'text/plain')).toBe(false);
    expect(isMarkdownExtractionSupported('archive.zip', 'application/zip')).toBe(false);
    expect(isMarkdownExtractionSupported('report.pdf', 'application/zip')).toBe(false);
  });

  it('allows missing content type when extension is supported', () => {
    expect(isMarkdownExtractionSupported('scan.png', '')).toBe(true);
  });

  it('describes supported extraction and transcription formats', () => {
    expect(getSupportedFileTypesDescription()).toContain('PDF');
    expect(getSupportedFileTypesDescription()).toContain('MP4');
  });
});
