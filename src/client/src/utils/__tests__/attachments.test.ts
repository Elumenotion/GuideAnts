import { describe, it, expect } from 'vitest';
import { mapContentType, uploadTypeToServer } from '../attachments';

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
});
