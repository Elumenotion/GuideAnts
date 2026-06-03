import { describe, expect, it } from 'vitest';
import {
  isDocumentServerSupportedByContentType,
  isDocumentServerSupportedByExtension,
  looksLikeDocumentServerFile,
  type DocumentServerCapabilities,
} from '../documentServer';

const enabledCapabilities: DocumentServerCapabilities = {
  enabled: true,
  publicUrl: 'http://localhost:8082',
  supportedExtensions: ['docx', 'pdf'],
  supportedContentTypes: [
    'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    'application/pdf',
  ],
};

describe('documentServer PDF exclusions', () => {
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
});
