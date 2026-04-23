import { useState, useCallback } from 'react';
import { useToast } from '../components/common/Toast';
import { api } from '../services/api';

interface UseDownloadLineageResult {
    download: (eventId: string, suggestedFileName?: string) => Promise<void>;
    isDownloading: boolean;
}

export function useDownloadLineage(): UseDownloadLineageResult {
    const [isDownloading, setIsDownloading] = useState<boolean>(false);
    const { showToast } = useToast();

    const download = useCallback(async (eventId: string, suggestedFileName?: string) => {
        if (isDownloading) return;
        try {
            setIsDownloading(true);
            const { blob, fileName } = await api.lineage.download(eventId);
            const url = URL.createObjectURL(blob);
            const anchor = document.createElement('a');
            anchor.href = url;
            anchor.download = suggestedFileName || fileName || 'download';
            document.body.appendChild(anchor);
            anchor.click();
            anchor.remove();
            setTimeout(() => URL.revokeObjectURL(url), 1000);
        } catch (err) {
            console.error('Failed to download lineage file:', err);
            showToast({
                type: 'error',
                title: 'Failed to download file',
                message: 'Please try again.'
            });
        } finally {
            setIsDownloading(false);
        }
    }, [isDownloading]);

    return { download, isDownloading };
} 