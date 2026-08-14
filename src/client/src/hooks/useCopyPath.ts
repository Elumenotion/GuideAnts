import { useCallback } from 'react';
import { useToast } from '../components/common/Toast';
import { copyTextToClipboard } from '../utils/clipboard';
import { toCwdRelativePath } from '../utils/cwdRelativePath';

interface UseCopyPathResult {
    copyPaths: (paths: string[]) => Promise<void>;
}

/**
 * Copies one or more notebook-root-relative paths to the clipboard as
 * sandbox-CWD-relative paths (see `toCwdRelativePath`), newline-joined, and
 * reports the result via toast. Shared by the project and notebook file trees.
 */
export function useCopyPath(): UseCopyPathResult {
    const { showToast } = useToast();

    const copyPaths = useCallback(async (paths: string[]) => {
        const normalized = paths.filter(Boolean).map(toCwdRelativePath).filter(Boolean);
        if (normalized.length === 0) return;
        const text = normalized.join('\n');
        const copied = await copyTextToClipboard(text);
        showToast(copied
            ? { type: 'success', title: normalized.length > 1 ? `${normalized.length} paths copied` : 'Path copied', message: text, duration: 3000 }
            : { type: 'error', title: 'Copy failed', message: 'Unable to copy to the clipboard.', duration: 5000 });
    }, [showToast]);

    return { copyPaths };
}
