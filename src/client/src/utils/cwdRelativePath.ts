/**
 * Convert a notebook-root-relative path into a path usable from the sandbox CWD
 * (unpublished notebooks: CWD is Output/).
 *
 * Mirrors server `ContextOptionFilesResolver.ToCwdRelativePath(..., isPublished: false)`:
 * - `Output/foo/bar` → `foo/bar`
 * - `Shared/docs` → `../Shared/docs`
 */
export function toCwdRelativePath(notebookRelativePath: string): string {
    const normalized = notebookRelativePath
        .replace(/\\/g, '/')
        .trim()
        .replace(/^\/+/, '');

    if (!normalized) {
        return normalized;
    }

    const outputPrefix = 'Output/';
    if (normalized.length >= outputPrefix.length
        && normalized.slice(0, outputPrefix.length).toLowerCase() === outputPrefix.toLowerCase()) {
        return normalized.slice(outputPrefix.length);
    }

    return `../${normalized}`;
}
