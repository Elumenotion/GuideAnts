import { describe, expect, it } from 'vitest';
import {
  getNotebookPathCandidates,
  normalizeNotebookRelativePath,
  notebookPathMatches,
} from '../notebookPath';

describe('notebookPath', () => {
  it('normalizes separators and leading prefixes', () => {
    expect(normalizeNotebookRelativePath('./docs\\guide.md')).toBe('docs/guide.md');
    expect(normalizeNotebookRelativePath('/Output/docs/guide.md')).toBe('Output/docs/guide.md');
  });

  it('builds Output-prefixed and unprefixed candidates', () => {
    expect(getNotebookPathCandidates('docs/guide.md')).toEqual([
      'docs/guide.md',
      'Output/docs/guide.md',
    ]);

    expect(getNotebookPathCandidates('Output/docs/guide.md')).toEqual([
      'Output/docs/guide.md',
      'docs/guide.md',
    ]);
  });

  it('matches CWD-relative assistant paths to Output tree paths', () => {
    expect(notebookPathMatches('Output/docs/guideants-product-sheet-v2.md', 'docs/guideants-product-sheet-v2.md')).toBe(true);
  });

  it('matches case-insensitively', () => {
    expect(notebookPathMatches('output/docs/Guide.md', 'Output/docs/guide.md')).toBe(true);
  });
});
