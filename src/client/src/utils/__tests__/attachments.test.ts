import { describe, it, expect } from 'vitest';
import {
  fileTypeFromUploadType,
  mapContentType,
  normalizeRelativePath,
  toPendingUploadType,
  toSendAttachment,
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

describe('toSendAttachment', () => {
  // Regression: after an undo, the restored chip carries BOTH a real file id and a
  // relativePath (the server's projector fills both). The wire ref MUST be sent by id
  // so the server expands it to image pixels -- keying off relativePath (the old
  // behaviour) dropped the id and the model saw only a path line.
  it('sends a real-id attachment by id, dropping relativePath', () => {
    const ref = toSendAttachment({
      notebookFileId: '33b46048-0fe3-4eeb-a194-dbc358ba2911',
      relativePath: 'pasted-1792614546485-67yf.png',
      uploadType: 'image',
    });
    expect(ref).toEqual({
      notebookFileId: '33b46048-0fe3-4eeb-a194-dbc358ba2911',
      relativePath: null,
      uploadType: 'ImageFile',
    });
  });

  it('sends a path-sentinel attachment by path with a null id', () => {
    const ref = toSendAttachment({
      notebookFileId: 'path:Host/Mount.txt',
      relativePath: 'Host/Mount.txt',
      uploadType: 'text',
    });
    expect(ref).toEqual({
      notebookFileId: null,
      relativePath: 'Host/Mount.txt',
      uploadType: 'TextFile',
    });
  });

  it('recovers the path from the sentinel id when relativePath is absent', () => {
    const ref = toSendAttachment({
      notebookFileId: 'path:Assets\reference-pack',
      uploadType: 'folder',
    });
    expect(ref).toEqual({
      notebookFileId: null,
      relativePath: 'Assets/reference-pack',
      uploadType: 'Folder',
    });
  });
});
