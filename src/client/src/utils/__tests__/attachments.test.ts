import { describe, it, expect } from 'vitest';
import {
  fileTypeFromUploadType,
  mapContentType,
  normalizeRelativePath,
  toPendingUploadType,
  uploadTypeToServer,
} from '../attachments';

describe('attachments', () => {
  describe('mapContentType', () => {
    it.each([
      ['photo.png', 'image'],
      ['photo.JPG', 'image'],
      ['clip.mp3', 'audio'],
      ['notes.txt', 'text'],
      ['script.ts', 'text'],
      ['archive.zip', 'other'],
      ['noextension', 'other'],
    ])('maps %s to %s', (fileName, expected) => {
      expect(mapContentType(fileName)).toBe(expected);
    });
  });

  describe('uploadTypeToServer', () => {
    it.each([
      ['image', 'ImageFile'],
      ['audio', 'AudioFile'],
      ['text', 'TextFile'],
      ['other', 'SandboxFile'],
    ] as const)('maps %s to %s', (uploadType, expected) => {
      expect(uploadTypeToServer(uploadType)).toBe(expected);
    });
  });

  describe('toPendingUploadType', () => {
    it.each([
      ['ImageFile', 'image'],
      ['ImageUrl', 'image'],
      ['AudioFile', 'audio'],
      ['TextFile', 'text'],
      ['Folder', 'folder'],
      ['SandboxFile', 'other'],
    ] as const)('maps %s to %s', (uploadType, expected) => {
      expect(toPendingUploadType(uploadType, 'ignored.bin')).toBe(expected);
    });

    it('infers the type only for legacy null upload types', () => {
      expect(toPendingUploadType(null, 'legacy.png')).toBe('image');
      expect(toPendingUploadType(undefined, 'legacy.md')).toBe('text');
    });
  });

  it('normalizes separators and leading slashes while preserving case', () => {
    expect(normalizeRelativePath(' \\Data\\Report.CSV ')).toBe('Data/Report.CSV');
  });

  it.each([
    ['image', 'image'],
    ['audio', 'audio'],
    ['text', 'text'],
    ['folder', 'folder'],
    ['other', 'other'],
  ] as const)('derives file type %s', (uploadType, expected) => {
    expect(fileTypeFromUploadType(uploadType)).toBe(expected);
  });
});
