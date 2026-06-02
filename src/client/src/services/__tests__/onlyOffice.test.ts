import { describe, expect, it } from 'vitest';
import {
  isOnlyOfficeSupportedByContentType,
  isOnlyOfficeSupportedByExtension,
  looksLikeOnlyOfficeFile,
  type OnlyOfficeCapabilities,
} from '../onlyOffice';

const enabledCapabilities: OnlyOfficeCapabilities = {
  enabled: true,
  publicUrl: 'http://localhost:8082',
  supportedExtensions: ['docx', 'pdf'],
  supportedContentTypes: [
    'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    'application/pdf',
  ],
};

describe('onlyOffice PDF exclusions', () => {
  it('does not route PDF by extension to ONLYOFFICE', () => {
    expect(isOnlyOfficeSupportedByExtension('sample.pdf', enabledCapabilities)).toBe(false);
    expect(isOnlyOfficeSupportedByExtension('sample.docx', enabledCapabilities)).toBe(true);
  });

  it('does not route PDF by content type to ONLYOFFICE', () => {
    expect(isOnlyOfficeSupportedByContentType('application/pdf', enabledCapabilities)).toBe(false);
    expect(
      isOnlyOfficeSupportedByContentType(
        'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
        enabledCapabilities
      )
    ).toBe(true);
  });

  it('does not classify PDF as an ONLYOFFICE candidate', () => {
    expect(looksLikeOnlyOfficeFile('sample.pdf', 'application/pdf')).toBe(false);
    expect(
      looksLikeOnlyOfficeFile(
        'sample.docx',
        'application/vnd.openxmlformats-officedocument.wordprocessingml.document'
      )
    ).toBe(true);
  });
});
