import { describe, it, expect } from 'vitest';
import { toCwdRelativePath } from '../cwdRelativePath';

describe('toCwdRelativePath', () => {
    it('prefixes notebook-root paths with ../', () => {
        expect(toCwdRelativePath('Shared/docs')).toBe('../Shared/docs');
        expect(toCwdRelativePath('readme.txt')).toBe('../readme.txt');
    });

    it('strips a leading Output/ segment (CWD is Output/)', () => {
        expect(toCwdRelativePath('Output/result.txt')).toBe('result.txt');
        expect(toCwdRelativePath('Output/nested/out.txt')).toBe('nested/out.txt');
    });

    it('normalizes slashes and drops a leading slash', () => {
        expect(toCwdRelativePath('\\Shared\\docs')).toBe('../Shared/docs');
        expect(toCwdRelativePath('/Shared/docs')).toBe('../Shared/docs');
    });

    it('returns empty for blank input', () => {
        expect(toCwdRelativePath('')).toBe('');
        expect(toCwdRelativePath('   ')).toBe('');
    });
});
