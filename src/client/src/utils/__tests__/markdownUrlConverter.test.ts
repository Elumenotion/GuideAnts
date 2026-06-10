import { describe, it, expect } from 'vitest';
import {
  convertAbsoluteToRelative,
  convertRelativeToAbsolute,
  isRelativePath,
  isNotebookFileUrl,
  extractContextFromUrl,
} from '../markdownUrlConverter';

const PROJECT_ID = '11111111-1111-1111-1111-111111111111';
const NOTEBOOK_ID = '22222222-2222-2222-2222-222222222222';
const API_BASE = 'http://localhost/api';

describe('markdownUrlConverter', () => {
  describe('isRelativePath', () => {
    it('identifies relative paths and excludes absolute URLs', () => {
      expect(isRelativePath('image.png')).toBe(true);
      expect(isRelativePath('./docs/guide.md')).toBe(true);
      expect(isRelativePath('https://example.com/a.png')).toBe(false);
      expect(isRelativePath('/absolute/path.png')).toBe(false);
      expect(isRelativePath('mailto:test@example.com')).toBe(false);
      expect(isRelativePath('data:image/png;base64,abc')).toBe(false);
      expect(isRelativePath('')).toBe(false);
    });
  });

  describe('isNotebookFileUrl', () => {
    it('detects notebook file content URLs', () => {
      const url = `${API_BASE}/projects/${PROJECT_ID}/notebooks/${NOTEBOOK_ID}/files/content?path=Output%2Fchart.png`;
      expect(isNotebookFileUrl(url)).toBe(true);
      expect(isNotebookFileUrl('https://example.com/other.png')).toBe(false);
    });
  });

  describe('extractContextFromUrl', () => {
    it('extracts project and notebook ids from notebook file URLs', () => {
      const url = `${API_BASE}/projects/${PROJECT_ID}/notebooks/${NOTEBOOK_ID}/files/content?path=foo`;
      expect(extractContextFromUrl(url)).toEqual({
        projectId: PROJECT_ID,
        notebookId: NOTEBOOK_ID,
      });
      expect(extractContextFromUrl('not-a-url')).toBeNull();
    });
  });

  describe('convertAbsoluteToRelative', () => {
    it('converts markdown image and link URLs to relative paths', () => {
      const absolute = `${API_BASE}/projects/${PROJECT_ID}/notebooks/${NOTEBOOK_ID}/files/content?path=Output%2Fchart.png&m=123`;
      const markdown = `![chart](${absolute}) and [doc](${absolute})`;
      const result = convertAbsoluteToRelative(markdown);

      expect(result).toContain('![chart](./Output/chart.png)');
      expect(result).toContain('[doc](./Output/chart.png)');
    });

    it('converts HTML media src attributes for notebook URLs', () => {
      const absolute = `${API_BASE}/projects/${PROJECT_ID}/notebooks/${NOTEBOOK_ID}/files/content?path=media%2Fclip.mp4`;
      const html = `<video controls src="${absolute}"></video>`;
      const result = convertAbsoluteToRelative(html);

      expect(result).toBe('<video controls src="./media/clip.mp4"></video>');
    });

    it('returns empty input unchanged', () => {
      expect(convertAbsoluteToRelative('')).toBe('');
    });

    it('leaves malformed absolute URLs unchanged', () => {
      const markdown = '![bad](not-a-valid-url)';
      expect(convertAbsoluteToRelative(markdown)).toBe(markdown);
    });
  });

  describe('convertRelativeToAbsolute', () => {
    it('converts relative markdown paths to authenticated notebook URLs', () => {
      const markdown = '![img](./Output/chart.png) and [link](../notes/readme.md)';
      const result = convertRelativeToAbsolute(markdown, PROJECT_ID, NOTEBOOK_ID, API_BASE);

      expect(result).toContain(
        `${API_BASE}/projects/${PROJECT_ID}/notebooks/${NOTEBOOK_ID}/files/content?path=Output%2Fchart.png`,
      );
      expect(result).toContain(
        `${API_BASE}/projects/${PROJECT_ID}/notebooks/${NOTEBOOK_ID}/files/content?path=notes%2Freadme.md`,
      );
    });

    it('skips fragments, mailto links, and external URLs', () => {
      const markdown = '[anchor](#section) [mail](mailto:a@b.com) [ext](https://x.com/a.png)';
      const result = convertRelativeToAbsolute(markdown, PROJECT_ID, NOTEBOOK_ID, API_BASE);
      expect(result).toBe(markdown);
    });

    it('converts relative HTML media src attributes', () => {
      const html = '<audio src="./audio/note.mp3" controls></audio>';
      const result = convertRelativeToAbsolute(html, PROJECT_ID, NOTEBOOK_ID, API_BASE);
      expect(result).toContain(
        `${API_BASE}/projects/${PROJECT_ID}/notebooks/${NOTEBOOK_ID}/files/content?path=audio%2Fnote.mp3`,
      );
    });

    it('returns input when project or notebook id is missing', () => {
      const markdown = '![img](./a.png)';
      expect(convertRelativeToAbsolute(markdown, '', NOTEBOOK_ID, API_BASE)).toBe(markdown);
      expect(convertRelativeToAbsolute(markdown, PROJECT_ID, '', API_BASE)).toBe(markdown);
    });
  });
});
