/**
 * Copies text to the clipboard, falling back to a hidden-textarea + execCommand
 * approach when the async Clipboard API is unavailable (e.g. non-secure/HTTP
 * origins on a LAN, or older browsers).
 *
 * Returns true on success, false on failure. Does not throw.
 */
export async function copyTextToClipboard(text: string): Promise<boolean> {
    try {
        if (navigator.clipboard && navigator.clipboard.writeText) {
            await navigator.clipboard.writeText(text);
            return true;
        }

        const textArea = document.createElement('textarea');
        textArea.value = text;
        textArea.style.position = 'fixed';
        textArea.style.left = '-999999px';
        textArea.style.top = '-999999px';
        document.body.appendChild(textArea);
        textArea.focus();
        textArea.select();

        try {
            return document.execCommand('copy');
        } finally {
            document.body.removeChild(textArea);
        }
    } catch (error) {
        console.error('Failed to copy to clipboard:', error);
        return false;
    }
}
