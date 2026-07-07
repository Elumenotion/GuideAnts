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

  it('matches CWD-relative filenames to Runs folder paths', () => {
    expect(notebookPathMatches('Runs/ABC123/friendly_duck.png', 'friendly_duck.png')).toBe(true);
  });

  it('matches CWD-relative assistant paths to Output tree paths', () => {
    expect(notebookPathMatches('Output/docs/guideants-product-sheet-v2.md', 'docs/guideants-product-sheet-v2.md')).toBe(true);
  });

  it('matches case-insensitively', () => {
    expect(notebookPathMatches('output/docs/Guide.md', 'Output/docs/guide.md')).toBe(true);
  });

  it('returns no candidates for blank paths and rejects empty candidate matches', () => {
    expect(getNotebookPathCandidates('   ')).toEqual([]);
    expect(notebookPathMatches('', 'docs/guide.md')).toBe(false);
  });

  it('handles output-prefixed candidate generation', () => {
    expect(getNotebookPathCandidates('output/report.md')).toEqual([
      'output/report.md',
      'report.md',
      'Output/report.md',
    ]);
  });

  it('falls back when notebook paths contain invalid URI escapes', () => {
    expect(normalizeNotebookRelativePath('%E0%A4%A')).toBe('%E0%A4%A');
    expect(notebookPathMatches('docs/%E0%A4%A', '%E0%A4%A')).toBe(true);
    expect(notebookPathMatches('docs/report.md', '   ')).toBe(false);
    expect(notebookPathMatches('runs/abc/duck.png', 'duck.png')).toBe(true);
    expect(notebookPathMatches('duck.png', 'duck.png')).toBe(true);
  });
});
